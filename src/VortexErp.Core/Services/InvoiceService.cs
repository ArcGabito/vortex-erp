using Microsoft.Data.Sqlite;
using VortexApi.Suite.Auditing;
using VortexApi.Suite.Taxes;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Sync;

namespace VortexErp.Core.Services;

/// <summary>
/// Comprobantes (factura o boleta) y cobranzas. Se emiten desde un pedido o desde una venta de caja del POS y publican
/// <c>invoice.issued</c>; cada cobro publica <c>payment.received</c>. El CRM y el POS los muestran.
/// </summary>
public sealed class InvoiceService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    public const int DefaultCreditDays = 30;

    // ---------- Emisión ----------

    public Task<string> IssueFromOrderAsync(Guid orderId, string kind, int creditDays = DefaultCreditDays, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.IssueInvoice);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var order = await OrderService.GetAsync(c, tx, orderId);
            Require(order is not null, "El pedido no existe.");
            Require(order!.CanInvoice, order.InvoiceId is not null ? "El pedido ya tiene comprobante." : "Este pedido no se puede facturar.");
            var lines = await OrderService.LinesAsync(c, tx, orderId);
            var codes = await TaxCodesAsync(c, tx, lines.Select(l => l.ItemId));
            var invoiceLines = lines.Select(l => new InvoiceLine(l.ItemId, l.ItemName, l.Quantity, l.UnitPrice, l.Total, codes[l.ItemId])).ToList();

            var (id, number) = await IssueAsync(c, tx, now, kind, order.CustomerId, orderId, null, order.CurrencyCode, invoiceLines,
                paid: 0, creditDays);
            await c.ExecAsync("UPDATE sales_orders SET invoice_id = $invoice WHERE id = $id", tx, ("$id", orderId), ("$invoice", id));
            return number;
        }, ct);
    }

    /// <summary>
    /// Comprobante de una venta de caja. Lo cobrado en caja queda pagado; solo la parte al crédito queda por cobrar.
    /// El POS anota el número en la venta al recibir <c>invoice.issued</c>.
    /// </summary>
    public Task<string> IssueFromSaleAsync(Guid saleId, string kind, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.IssueInvoice);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var sale = await PosSaleAsync(c, tx, saleId);
            Require(sale is not null, "La venta no existe.");
            Require(sale!.CanInvoice, sale.InvoiceId is not null ? "La venta ya tiene comprobante." : "La venta está anulada.");
            Require(sale.Returned == 0, "La venta tiene devoluciones: emite el comprobante por lo que quedó en un pedido.");
            var lines = await c.QueryAsync("SELECT * FROM pos_sale_lines WHERE sale_id = $id ORDER BY line_no", r =>
                new InvoiceLine(r.Guid("item_id"), r.StrOrNull("name") ?? "Artículo", r.Dec("quantity"), r.Dec("unit_price"), r.Dec("total"),
                    r.StrOrNull("tax_code") ?? (r.Bool("taxable") ? TaxCategories.Standard : TaxCategories.Exempt)), tx, ("$id", saleId));

            var (id, number) = await IssueAsync(c, tx, now, kind, sale.CustomerId, null, saleId, sale.CurrencyCode,
                lines, paid: sale.Total - sale.Credit,
                creditDays: sale.Credit > 0 ? DefaultCreditDays : 0);
            await c.ExecAsync("UPDATE pos_sales SET invoice_id = $invoice WHERE id = $id", tx, ("$id", saleId), ("$invoice", id));
            return number;
        }, ct);
    }

    private async Task<(Guid Id, string Number)> IssueAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind,
        Guid? customerId, Guid? orderId, Guid? saleId, string currency, IReadOnlyList<InvoiceLine> lines, decimal paid, int creditDays)
    {
        Require(kind is InvoiceKinds.Invoice or InvoiceKinds.Receipt, "Elige factura o boleta.");
        Require(lines.Count > 0, "El documento no tiene líneas.");
        if (kind == InvoiceKinds.Invoice)
        {
            var docType = customerId is null ? null
                : await c.ScalarAsync<string?>("SELECT document_type FROM parties WHERE id = $id", tx, ("$id", customerId));
            Require(docType == "RUC", "La factura requiere un cliente con RUC. Para personas emite una boleta.");
        }

        // Impuesto del país de la empresa, desglosado por tasa y redondeado con la moneda del comprobante.
        var breakdown = TaxCalculator.Calculate(lines.Select(l => new TaxableLine(l.Total, l.TaxCode)), await TaxRegimeAsync(c, tx), currency);
        var (subtotal, tax, total) = (breakdown.Net, breakdown.Tax, breakdown.Total);
        var id = Guid.NewGuid();
        var number = await DocumentNumbers.NextInvoiceAsync(c, tx, kind, Context.DeviceTag);
        var dueDate = DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime).AddDays(Math.Max(0, creditDays));
        var e = new InvoiceIssued(id, number, customerId, saleId, orderId, now, total, currency, kind, subtotal, tax, dueDate, lines,
            Math.Min(paid, total));
        await InsertInvoiceAsync(c, tx, e, "local", Context.UserName);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.InvoiceIssued, e, subjectType: "invoice", subjectId: number, id: id);
        await AuditAsync(c, tx, now, AuditActions.Issued, "invoice", id, number, $"{(kind == InvoiceKinds.Invoice ? "Factura" : "Boleta")} por {total:0.##} {currency}");
        return (id, number);
    }

    // ---------- Cobranza ----------

    public Task<string> RegisterPaymentAsync(Guid invoiceId, decimal amount, string method, string? reference, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.RegisterPayment);
        Require(amount > 0, "El monto debe ser mayor que cero.");
        Require(PaymentMethods.All.Contains(method), "Medio de pago desconocido.");
        return InTransactionAsync(async (c, tx, now) =>
    {
        var invoice = await GetAsync(c, tx, invoiceId);
        Require(invoice is not null, "El comprobante no existe.");
        Require(amount <= invoice!.Balance, $"El saldo del comprobante es {invoice.Balance:0.00}.");

        var id = Guid.NewGuid();
        var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Payment, Context.DeviceTag);
        var e = new PaymentReceived(id, invoice.CustomerId, invoiceId, invoice.SaleId, amount, invoice.CurrencyCode, method, number, Clean(reference));
        await InsertPaymentAsync(c, tx, e, now, Context.UserName);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.PaymentReceived, e, subjectType: "invoice", subjectId: invoice.Number, id: id);
        await AuditAsync(c, tx, now, AuditActions.PaymentRegistered, "invoice", invoiceId, invoice.Number, $"{number}: {amount:0.##} {invoice.CurrencyCode} ({method})");
        return number;
    }, ct);
    }

    // ---------- Consultas ----------

    public async Task<PagedResult<InvoiceRow>> GetInvoicesAsync(InvoiceQuery q, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE ($kind IS NULL OR i.kind = $kind) AND ($unpaid IS NULL OR (i.total - i.paid > 0) = $unpaid)
              AND ($customer IS NULL OR i.customer_id = $customer)
              AND ($search IS NULL OR i.number LIKE $like OR p.name LIKE $like OR p.document_number LIKE $like)
            """;
        var args = new (string, object?)[]
        {
            ("$kind", Clean(q.Kind)), ("$unpaid", q.Unpaid), ("$customer", q.CustomerId), ("$search", Clean(q.Search)),
            ("$like", Like(q.Search)), ("$limit", q.PageSize), ("$offset", (q.Page - 1) * q.PageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM invoices i LEFT JOIN parties p ON p.id = i.customer_id {where}", null, args);
        var rows = await c.QueryAsync($"{Select} {where} ORDER BY i.issued_at DESC LIMIT $limit OFFSET $offset", Read, null, args);
        return new PagedResult<InvoiceRow>(rows, total, q.Page, q.PageSize);
    }

    public async Task<InvoiceRow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await GetAsync(c, null, id);
    }

    public async Task<IReadOnlyList<InvoiceLineRow>> GetLinesAsync(Guid invoiceId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("SELECT * FROM invoice_lines WHERE invoice_id = $id ORDER BY line_no", r => new InvoiceLineRow(
            r.Int("line_no"), r.GuidOrNull("item_id"), r.Str("description"), r.Dec("quantity"), r.Dec("unit_price"), r.Dec("total")),
            null, ("$id", invoiceId));
    }

    public async Task<PagedResult<PaymentRow>> GetPaymentsAsync(Guid? invoiceId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var total = await c.ScalarAsync<int>("SELECT COUNT(*) FROM payments WHERE $invoice IS NULL OR invoice_id = $invoice", null,
            ("$invoice", invoiceId));
        var rows = await c.QueryAsync("""
            SELECT y.*, p.name AS customer_name, i.number AS invoice_number
            FROM payments y LEFT JOIN parties p ON p.id = y.customer_id LEFT JOIN invoices i ON i.id = y.invoice_id
            WHERE $invoice IS NULL OR y.invoice_id = $invoice
            ORDER BY y.received_at DESC LIMIT $limit OFFSET $offset
            """, r => new PaymentRow(r.Guid("id"), r.Str("number"), r.GuidOrNull("customer_id"), r.StrOrNull("customer_name"),
                r.GuidOrNull("invoice_id"), r.StrOrNull("invoice_number"), r.Dec("amount"), r.Str("currency_code"), r.Str("method"),
                r.StrOrNull("reference"), r.Time("received_at"), r.StrOrNull("user_name")), null,
            ("$invoice", invoiceId), ("$limit", pageSize), ("$offset", (page - 1) * pageSize));
        return new PagedResult<PaymentRow>(rows, total, page, pageSize);
    }

    /// <summary>Cuentas por cobrar por cliente, la de más vencido primero.</summary>
    public async Task<IReadOnlyList<ReceivableRow>> GetReceivablesAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT i.customer_id, COALESCE(p.name, 'Clientes varios') AS customer_name, COUNT(*) AS invoices,
                   SUM(i.total - i.paid) AS balance,
                   SUM(CASE WHEN i.due_date < $today THEN i.total - i.paid ELSE 0 END) AS overdue,
                   MIN(i.due_date) AS oldest_due
            FROM invoices i LEFT JOIN parties p ON p.id = i.customer_id
            WHERE i.total - i.paid > 0
            GROUP BY i.customer_id ORDER BY overdue DESC, balance DESC
            """, r => new ReceivableRow(r.GuidOrNull("customer_id"), r.Str("customer_name"), r.Int("invoices"), r.Dec("balance"),
                r.Dec("overdue"), r.StrOrNull("oldest_due") is { } d ? DateOnly.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : null),
            null, ("$today", Today()));
    }

    public async Task<PagedResult<PosSaleRow>> GetPosSalesAsync(bool uninvoicedOnly, int page, int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = "WHERE ($only = 0 OR (s.invoice_id IS NULL AND s.status = 'completed'))";
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM pos_sales s {where}", null, ("$only", uninvoicedOnly));
        var rows = await c.QueryAsync($"{SelectSales} {where} ORDER BY s.created_at DESC LIMIT $limit OFFSET $offset", ReadSale, null,
            ("$only", uninvoicedOnly), ("$limit", pageSize), ("$offset", (page - 1) * pageSize));
        return new PagedResult<PosSaleRow>(rows, total, page, pageSize);
    }

    // ---------- Lo que llega de otros sistemas y de otros equipos del ERP ----------

    internal static async Task ApplyInvoiceIssuedAsync(SqliteConnection c, SqliteTransaction tx, InvoiceIssued e)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM invoices WHERE id = $id", tx, ("$id", e.InvoiceId)) > 0)
            return;
        await InsertInvoiceAsync(c, tx, e, "remote", null);
        if (e.OrderId is { } order)
            await c.ExecAsync("UPDATE sales_orders SET invoice_id = $invoice WHERE id = $id", tx, ("$id", order), ("$invoice", e.InvoiceId));
        if (e.SaleId is { } sale)
            await c.ExecAsync("UPDATE pos_sales SET invoice_id = $invoice WHERE id = $id", tx, ("$id", sale), ("$invoice", e.InvoiceId));
    }

    internal static async Task ApplyPaymentReceivedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, PaymentReceived e)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM payments WHERE id = $id", tx, ("$id", e.PaymentId)) > 0)
            return;
        await InsertPaymentAsync(c, tx, e, at, null);
    }

    internal static async Task ApplySaleCompletedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, SaleCompleted e)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM pos_sales WHERE id = $id", tx, ("$id", e.SaleId)) > 0)
            return;
        var credit = (e.Payments ?? []).Where(p => p.Method == "credit").Sum(p => p.Amount);
        await c.ExecAsync("""
            INSERT INTO pos_sales (id, number, site_id, customer_id, status, currency_code, subtotal, tax, total, credit, created_at)
            VALUES ($id, $number, $site, $customer, 'completed', $currency, $subtotal, $tax, $total, $credit, $at)
            """, tx, ("$id", e.SaleId), ("$number", e.Number ?? e.SaleId.ToString()[..8].ToUpperInvariant()), ("$site", e.SiteId),
            ("$customer", e.CustomerId), ("$currency", e.CurrencyCode ?? "PEN"), ("$subtotal", e.Subtotal), ("$tax", e.Tax),
            ("$total", e.Total), ("$credit", credit), ("$at", at));
        var lineNo = 0;
        foreach (var l in (e.Lines ?? []).Where(l => l.ItemId != Guid.Empty))
        {
            var name = l.Name ?? await c.ScalarAsync<string?>("SELECT name FROM items WHERE id = $id", tx, ("$id", l.ItemId)) ?? l.Sku;
            await c.ExecAsync("""
                INSERT INTO pos_sale_lines (sale_id, line_no, item_id, name, quantity, unit_price, total, taxable, tax_code)
                VALUES ($sale, $no, $item, $name, $qty, $price, $total, $taxable, $taxCode)
                """, tx, ("$sale", e.SaleId), ("$no", ++lineNo), ("$item", l.ItemId), ("$name", name), ("$qty", l.Quantity),
                ("$price", l.UnitPrice), ("$total", l.Total), ("$taxable", l.Taxable),
                ("$taxCode", l.TaxCode ?? (l.Taxable ? TaxCategories.Standard : TaxCategories.Exempt)));
        }
    }

    internal static Task ApplySaleVoidedAsync(SqliteConnection c, SqliteTransaction tx, SaleVoided e) =>
        c.ExecAsync("UPDATE pos_sales SET status = 'voided' WHERE id = $id", tx, ("$id", e.SaleId));

    internal static Task ApplySaleReturnedAsync(SqliteConnection c, SqliteTransaction tx, SaleReturned e) =>
        c.ExecAsync("UPDATE pos_sales SET returned = returned + $total WHERE id = $id", tx, ("$id", e.SaleId), ("$total", Math.Abs(e.Total)));

    // ---------- Internos ----------

    private static async Task InsertInvoiceAsync(SqliteConnection c, SqliteTransaction tx, InvoiceIssued e, string origin, string? userName)
    {
        await c.ExecAsync("""
            INSERT INTO invoices (id, number, kind, origin, customer_id, order_id, sale_id, issued_at, due_date, currency_code, subtotal,
                                  tax, total, paid, user_name)
            VALUES ($id, $number, $kind, $origin, $customer, $order, $sale, $at, $due, $currency, $subtotal, $tax, $total, $paid, $user)
            """, tx, ("$id", e.InvoiceId), ("$number", e.Number), ("$kind", e.Kind ?? InvoiceKinds.Invoice), ("$origin", origin),
            ("$customer", e.CustomerId), ("$order", e.OrderId), ("$sale", e.SaleId), ("$at", e.IssuedAt),
            ("$due", e.DueDate?.ToString("yyyy-MM-dd")), ("$currency", e.CurrencyCode), ("$subtotal", e.Subtotal), ("$tax", e.Tax),
            ("$total", e.Total), ("$paid", e.Paid), ("$user", userName));
        var lineNo = 0;
        foreach (var l in e.Lines ?? [])
        {
            await c.ExecAsync("""
                INSERT INTO invoice_lines (invoice_id, line_no, item_id, description, quantity, unit_price, total, tax_code)
                VALUES ($id, $no, $item, $desc, $qty, $price, $total, $tax)
                """, tx, ("$id", e.InvoiceId), ("$no", ++lineNo), ("$item", l.ItemId), ("$desc", l.Description), ("$qty", l.Quantity),
                ("$price", l.UnitPrice), ("$total", l.Total), ("$tax", l.TaxCode));
        }
    }

    private static async Task InsertPaymentAsync(SqliteConnection c, SqliteTransaction tx, PaymentReceived e, DateTimeOffset at, string? userName)
    {
        await c.ExecAsync("""
            INSERT INTO payments (id, number, customer_id, invoice_id, sale_id, amount, currency_code, method, reference, received_at, user_name)
            VALUES ($id, $number, $customer, $invoice, $sale, $amount, $currency, $method, $ref, $at, $user)
            """, tx, ("$id", e.PaymentId), ("$number", e.Number ?? e.PaymentId.ToString()[..8].ToUpperInvariant()), ("$customer", e.CustomerId),
            ("$invoice", e.InvoiceId), ("$sale", e.SaleId), ("$amount", e.Amount), ("$currency", e.CurrencyCode), ("$method", e.Method),
            ("$ref", e.Reference), ("$at", at), ("$user", userName));
        if (e.InvoiceId is { } invoice)
            await c.ExecAsync("UPDATE invoices SET paid = MIN(total, paid + $amount) WHERE id = $id", tx, ("$id", invoice), ("$amount", e.Amount));
    }

    /// <summary>Categoría de impuesto de cada artículo (la del hub; sin dato, tasa general).</summary>
    private static async Task<Dictionary<Guid, string>> TaxCodesAsync(SqliteConnection c, SqliteTransaction tx, IEnumerable<Guid> items)
    {
        var codes = new Dictionary<Guid, string>();
        foreach (var id in items.Distinct())
            codes[id] = TaxCategories.Normalize(await c.ScalarAsync<string?>("SELECT tax_code FROM items WHERE id = $id", tx, ("$id", id)));
        return codes;
    }


    private string Today() => DateOnly.FromDateTime(Clock.GetUtcNow().ToOffset(ErpClock.Offset).DateTime).ToString("yyyy-MM-dd");

    private const string Select = """
        SELECT i.*, p.name AS customer_name, p.document_number AS customer_document
        FROM invoices i LEFT JOIN parties p ON p.id = i.customer_id
        """;

    private static Task<InvoiceRow?> GetAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        c.FirstOrDefaultAsync($"{Select} WHERE i.id = $id", Read, tx, ("$id", id));

    private static InvoiceRow Read(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("number"), r.Str("kind"), r.Str("origin"), r.GuidOrNull("customer_id"), r.StrOrNull("customer_name"),
        r.StrOrNull("customer_document"), r.GuidOrNull("order_id"), r.GuidOrNull("sale_id"), r.Time("issued_at"),
        r.StrOrNull("due_date") is { } d ? DateOnly.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : null,
        r.Str("currency_code"), r.Dec("subtotal"), r.Dec("tax"), r.Dec("total"), r.Dec("paid"), r.StrOrNull("user_name"));

    private const string SelectSales = """
        SELECT s.*, t.name AS site_name, p.name AS customer_name, i.number AS invoice_number
        FROM pos_sales s LEFT JOIN sites t ON t.id = s.site_id LEFT JOIN parties p ON p.id = s.customer_id
             LEFT JOIN invoices i ON i.id = s.invoice_id
        """;

    private static Task<PosSaleRow?> PosSaleAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        c.FirstOrDefaultAsync($"{SelectSales} WHERE s.id = $id", ReadSale, tx, ("$id", id));

    private static PosSaleRow ReadSale(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("number"), r.GuidOrNull("site_id"), r.StrOrNull("site_name"), r.GuidOrNull("customer_id"),
        r.StrOrNull("customer_name"), r.Str("status"), r.Str("currency_code"), r.Dec("total"), r.Dec("credit"), r.Dec("returned"),
        r.Time("created_at"), r.GuidOrNull("invoice_id"), r.StrOrNull("invoice_number"));
}
