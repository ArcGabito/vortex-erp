using Microsoft.Data.Sqlite;
using VortexApi.Suite.Auditing;
using VortexApi.Suite.Money;
using VortexApi.Suite.Taxes;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Sync;

using static VortexErp.Core.Texts.CoreText;

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
            Require(order is not null, T("error.el_pedido_no_existe"));
            Require(order!.CanInvoice, order.InvoiceId is not null ? T("error.el_pedido_ya_tiene_comprobante") : T("error.este_pedido_no_se_puede"));
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
    /// Comprobante de un despacho (un pedido que salió en partes se factura por parte): las líneas son lo despachado al
    /// precio del pedido. No se puede si el pedido ya se facturó entero.
    /// </summary>
    public Task<string> IssueFromShipmentAsync(Guid shipmentId, string kind, int creditDays = DefaultCreditDays, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.IssueInvoice);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var orderId = await c.ScalarAsync<string?>("SELECT order_id FROM order_shipments WHERE id = $id", tx, ("$id", shipmentId));
            Require(orderId is not null, T("error.el_despacho_no_existe"));
            var order = (await OrderService.GetAsync(c, tx, Guid.Parse(orderId!)))!;
            Require(order.InvoiceId is null, T("error.el_pedido_ya_tiene_comprobante"));
            Require(order.CustomerId is not null && !order.PendingApproval, T("error.este_pedido_no_se_puede"));
            var shipment = (await OrderService.ShipmentsAsync(c, tx, order.Id, shipmentId)).Single();
            Require(shipment.InvoiceId is null, T("error.el_despacho_ya_tiene_comprobante", shipment.Number ?? ""));
            var codes = await TaxCodesAsync(c, tx, shipment.Lines.Select(l => l.ItemId));
            var invoiceLines = shipment.Lines.Select(l => new InvoiceLine(l.ItemId, l.ItemName, l.Quantity, l.UnitPrice, l.Total, codes[l.ItemId])).ToList();
            var (id, number) = await IssueAsync(c, tx, now, kind, order.CustomerId, order.Id, null, order.CurrencyCode, invoiceLines,
                paid: 0, creditDays, shipmentId);
            await c.ExecAsync("UPDATE order_shipments SET invoice_id = $invoice WHERE id = $id", tx, ("$id", shipmentId), ("$invoice", id));
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
            Require(sale is not null, T("error.la_venta_no_existe"));
            Require(sale!.CanInvoice, sale.InvoiceId is not null ? T("error.la_venta_ya_tiene_comprobante") : T("error.la_venta_esta_anulada"));
            Require(sale.Returned == 0, T("error.la_venta_tiene_devoluciones_emite"));
            var lines = await c.QueryAsync("SELECT * FROM pos_sale_lines WHERE sale_id = $id ORDER BY line_no", r =>
                new InvoiceLine(r.Guid("item_id"), r.StrOrNull("name") ?? "Artículo", r.Dec("quantity"), r.Dec("unit_price"), r.Dec("total"),
                    r.StrOrNull("tax_code") ?? (r.Bool("taxable") ? TaxCategories.Standard : TaxCategories.Exempt)), tx, ("$id", saleId));

            var (id, number) = await IssueAsync(c, tx, now, kind, sale.CustomerId, null, saleId, sale.CurrencyCode,
                lines, paid: sale.Total - sale.Credit,
                creditDays: sale.Credit > 0 ? await TermDaysAsync(c, tx, sale.CustomerId) : 0);
            await c.ExecAsync("UPDATE pos_sales SET invoice_id = $invoice WHERE id = $id", tx, ("$id", saleId), ("$invoice", id));
            return number;
        }, ct);
    }

    private async Task<(Guid Id, string Number)> IssueAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind,
        Guid? customerId, Guid? orderId, Guid? saleId, string currency, IReadOnlyList<InvoiceLine> lines, decimal paid, int creditDays,
        Guid? shipmentId = null)
    {
        Require(kind is InvoiceKinds.Invoice or InvoiceKinds.Receipt, T("error.elige_factura_boleta"));
        Require(lines.Count > 0, T("error.el_documento_no_tiene_lineas"));
        if (kind == InvoiceKinds.Invoice)
        {
            var docType = customerId is null ? null
                : await c.ScalarAsync<string?>("SELECT document_type FROM parties WHERE id = $id", tx, ("$id", customerId));
            Require(docType == "RUC", T("error.la_factura_requiere_un_cliente"));
        }

        // Tipo de cambio del día a la moneda de la empresa (de la copia local del hub): sin él no se emite en otra moneda.
        var company = await Fx.CompanyCurrencyAsync(c, tx);
        var rate = await Fx.RateAsync(c, tx, currency, company, DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime));
        Require(rate is not null, T("error.sin_tipo_de_cambio", currency, company));

        // Impuesto del país de la empresa, desglosado por tasa y redondeado con la moneda del comprobante.
        var breakdown = TaxCalculator.Calculate(lines.Select(l => new TaxableLine(l.Total, l.TaxCode)), await TaxRegimeAsync(c, tx), currency);
        var (subtotal, tax, total) = (breakdown.Net, breakdown.Tax, breakdown.Total);
        var id = Guid.NewGuid();
        var number = await DocumentNumbers.NextInvoiceAsync(c, tx, kind, Context.DeviceTag);
        var dueDate = DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime).AddDays(Math.Max(0, creditDays));
        var e = new InvoiceIssued(id, number, customerId, saleId, orderId, now, total, currency, kind, subtotal, tax, dueDate, lines,
            Math.Min(paid, total), rate == 1m ? null : rate, shipmentId);
        await InsertInvoiceAsync(c, tx, e, "local", Context.UserName);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.InvoiceIssued, e, subjectType: "invoice", subjectId: number, id: id);
        await AuditAsync(c, tx, now, AuditActions.Issued, "invoice", id, number, $"{(kind == InvoiceKinds.Invoice ? "Factura" : "Boleta")} por {total:0.##} {currency}");
        return (id, number);
    }

    // ---------- Cobranza ----------

    /// <summary>
    /// Cobro de un comprobante. <paramref name="amount"/> va en <paramref name="paidCurrency"/> (sin ella, la del comprobante).
    /// Un comprobante en otra moneda se puede cobrar en la de la empresa al tipo de cambio del día: se abona lo que eso vale
    /// en la moneda del comprobante y la diferencia con el tipo de la emisión queda como diferencia de cambio.
    /// </summary>
    public Task<string> RegisterPaymentAsync(Guid invoiceId, decimal amount, string method, string? reference, string? paidCurrency = null,
        CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.RegisterPayment);
        Require(amount > 0, T("error.el_monto_debe_ser_mayor"));
        Require(PaymentMethods.All.Contains(method), T("error.medio_de_pago_desconocido"));
        return InTransactionAsync(async (c, tx, now) =>
    {
        var invoice = await GetAsync(c, tx, invoiceId);
        Require(invoice is not null, T("error.el_comprobante_no_existe"));
        Require(!invoice!.IsVoided, T("error.el_comprobante_esta_anulado"));

        var applied = amount;
        decimal? paidAmount = null, rate = null, fx = null;
        var paid = string.IsNullOrWhiteSpace(paidCurrency) ? invoice.CurrencyCode : paidCurrency.Trim().ToUpperInvariant();
        if (paid != invoice.CurrencyCode)
        {
            var company = await Fx.CompanyCurrencyAsync(c, tx);
            Require(paid == company, T("error.cobro_solo_en_moneda", invoice.CurrencyCode, company));
            rate = await Fx.RateAsync(c, tx, invoice.CurrencyCode, company, DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime));
            Require(rate is not null, T("error.sin_tipo_de_cambio", invoice.CurrencyCode, company));
            // Lo que vale lo recibido en la moneda del comprobante; si cubre el saldo (con el redondeo), salda el comprobante.
            applied = Currencies.Get(invoice.CurrencyCode).Round(amount / rate!.Value);
            if (applied > invoice.Balance && Currencies.Get(company).Round(invoice.Balance * rate.Value) <= amount + 0.01m)
                applied = invoice.Balance;
            paidAmount = amount;
            // Diferencia de cambio: lo recibido contra lo que valía lo abonado el día de la emisión.
            fx = Currencies.Get(company).Round(amount - applied * (invoice.ExchangeRate ?? rate.Value));
        }
        Require(applied <= invoice.Balance, T("error.el_saldo_del_comprobante_es", invoice.Balance));

        var id = Guid.NewGuid();
        var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Payment, Context.DeviceTag);
        var e = new PaymentReceived(id, invoice.CustomerId, invoiceId, invoice.SaleId, applied, invoice.CurrencyCode, method, number, Clean(reference),
            paidAmount, paidAmount is null ? null : paid, rate, fx);
        await InsertPaymentAsync(c, tx, e, now, Context.UserName);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.PaymentReceived, e, subjectType: "invoice", subjectId: invoice.Number, id: id);
        await AuditAsync(c, tx, now, AuditActions.PaymentRegistered, "invoice", invoiceId, invoice.Number,
            paidAmount is null ? $"{number}: {amount:0.##} {invoice.CurrencyCode} ({method})"
                : $"{number}: {amount:0.##} {paid} = {applied:0.##} {invoice.CurrencyCode} a {rate:0.####} ({method}); diferencia de cambio {fx:0.##} {paid}");
        return number;
    }, ct);
    }

    /// <summary>Diferencia de cambio de los cobros entre <paramref name="from"/> y <paramref name="to"/> (fechas de la empresa, incluidas).</summary>
    public async Task<FxSummary> GetFxSummaryAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        static DateTimeOffset Utc(DateOnly d) => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), ErpClock.Offset).ToUniversalTime();
        var rows = await c.QueryAsync("""
            SELECT COALESCE(SUM(CASE WHEN fx_difference > 0 THEN fx_difference ELSE 0 END), 0) AS gains,
                   COALESCE(SUM(CASE WHEN fx_difference < 0 THEN -fx_difference ELSE 0 END), 0) AS losses,
                   COUNT(*) AS n
            FROM payments WHERE fx_difference IS NOT NULL AND received_at >= $from AND received_at < $to
            """, r => (Gains: r.Dec("gains"), Losses: r.Dec("losses"), Count: r.Int("n")), null,
            ("$from", Utc(from)), ("$to", Utc(to.AddDays(1))));
        return new FxSummary(await Fx.CompanyCurrencyAsync(c, null), rows[0].Gains, rows[0].Losses, rows[0].Count);
    }

    // ---------- Notas de crédito y débito ----------

    /// <summary>
    /// Nota de crédito vinculada al comprobante: rebaja su saldo (si ya estaba cobrado, queda saldo a favor del cliente).
    /// Con motivo <c>void</c> rebaja todo lo que queda; con <c>return</c>, las cantidades de las líneas indicadas; con
    /// <c>discount</c> o <c>correction</c>, importes libres.
    /// </summary>
    public Task<string> IssueCreditNoteAsync(Guid invoiceId, string reason, IReadOnlyList<NoteLineDraft> lines, string? description = null,
        CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.IssueNote);
        Require(NoteReasons.CreditReasons.Contains(reason), T("error.motivo_de_nota_desconocido"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var invoice = await GetAsync(c, tx, invoiceId);
            Require(invoice is not null, T("error.el_comprobante_no_existe"));
            Require(!invoice!.IsVoided, T("error.el_comprobante_esta_anulado"));
            var invoiceLines = await LinesAsync(c, tx, invoiceId);
            var drafts = reason == NoteReasons.Void
                ? invoiceLines.Where(l => l.Returnable > 0).Select(l => new NoteLineDraft(l.LineNo, l.Returnable)).ToList()
                : lines.ToList();
            Require(drafts.Count > 0, T("error.indica_que_rebaja_la_nota"));
            if (reason == NoteReasons.Return)
                Require(drafts.All(d => d.InvoiceLineNo is not null), T("error.la_devolucion_va_por_lineas"));

            var noteLines = BuildNoteLines(invoice, invoiceLines, drafts);
            if (reason == NoteReasons.Void)
                noteLines = await VoidLinesAsync(c, tx, invoice, invoiceLines, noteLines);
            return await IssueNoteAsync(c, tx, now, NoteKinds.Credit, invoice, reason, noteLines, description);
        }, ct);
    }

    /// <summary>Nota de débito vinculada al comprobante (intereses, aumento de valor, penalidad): suma a su saldo.</summary>
    public Task<string> IssueDebitNoteAsync(Guid invoiceId, string reason, decimal amount, string description, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.IssueNote);
        Require(NoteReasons.DebitReasons.Contains(reason), T("error.motivo_de_nota_desconocido"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var invoice = await GetAsync(c, tx, invoiceId);
            Require(invoice is not null, T("error.el_comprobante_no_existe"));
            Require(!invoice!.IsVoided, T("error.el_comprobante_esta_anulado"));
            var noteLines = BuildNoteLines(invoice, await LinesAsync(c, tx, invoiceId), [new NoteLineDraft(Amount: amount, Description: description)]);
            return await IssueNoteAsync(c, tx, now, NoteKinds.Debit, invoice, reason, noteLines, null);
        }, ct);
    }

    /// <summary>
    /// La anulación rebaja exactamente lo que queda del comprobante. Si antes hubo descuentos o débitos, las líneas ya no
    /// coinciden con ese saldo: se rebaja como un importe, con la categoría de impuesto del comprobante.
    /// </summary>
    private static async Task<List<NoteLine>> VoidLinesAsync(SqliteConnection c, SqliteTransaction tx, InvoiceRow invoice,
        IReadOnlyList<InvoiceLineRow> invoiceLines, List<NoteLine> lines)
    {
        var regime = await TaxRegimeAsync(c, tx);
        var total = TaxCalculator.Calculate(lines.Select(l => new TaxableLine(l.Total, l.TaxCode)), regime, invoice.CurrencyCode).Total;
        if (total == invoice.Creditable)
            return lines;
        var taxCode = invoiceLines.Select(l => l.TaxCode).FirstOrDefault(t => t is not null) ?? TaxCategories.Standard;
        var currency = VortexApi.Suite.Money.Currencies.Get(invoice.CurrencyCode);
        // Con precios sin impuesto (EE. UU.) el importe de la línea es la base: se quita el impuesto del saldo.
        var amount = regime.PricesIncludeTax ? invoice.Creditable : currency.Round(invoice.Creditable / (1 + regime.RateOf(taxCode)));
        return [new NoteLine(null, T("info.anulacion_de", invoice.Number), 1, amount, amount, taxCode)];
    }

    private static List<NoteLine> BuildNoteLines(InvoiceRow invoice, IReadOnlyList<InvoiceLineRow> invoiceLines, IReadOnlyList<NoteLineDraft> drafts)
    {
        var currency = VortexApi.Suite.Money.Currencies.Get(invoice.CurrencyCode);
        // Un importe libre lleva la categoría de impuesto del comprobante (la de su primera línea).
        var taxCode = invoiceLines.Select(l => l.TaxCode).FirstOrDefault(t => t is not null) ?? TaxCategories.Standard;
        var result = new List<NoteLine>();
        foreach (var group in drafts.GroupBy(d => d.InvoiceLineNo))
        {
            if (group.Key is { } no)
            {
                var line = invoiceLines.FirstOrDefault(l => l.LineNo == no);
                Require(line is not null, T("error.una_de_las_lineas_no"));
                var quantity = group.Sum(d => d.Quantity);
                Require(quantity > 0, T("error.las_cantidades_deben_ser_mayores"));
                Require(quantity <= line!.Returnable, T("error.de_solo_se_puede_rebajar", line.Description, line.Returnable));
                var total = quantity == line.Returnable && line.CreditedQty == 0 ? line.Total : currency.Round(line.Total * quantity / line.Quantity);
                result.Add(new NoteLine(line.ItemId, line.Description, quantity, line.UnitPrice, total, line.TaxCode ?? taxCode, no));
                continue;
            }
            foreach (var d in group)
            {
                Require(d.Amount > 0, T("error.el_monto_debe_ser_mayor"));
                Require(!string.IsNullOrWhiteSpace(d.Description), T("error.describe_el_importe_de_la_nota"));
                var amount = currency.Round(d.Amount);
                result.Add(new NoteLine(null, d.Description!.Trim(), 1, amount, amount, taxCode));
            }
        }
        return result;
    }

    private async Task<string> IssueNoteAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, InvoiceRow invoice,
        string reason, IReadOnlyList<NoteLine> lines, string? description)
    {
        var breakdown = TaxCalculator.Calculate(lines.Select(l => new TaxableLine(l.Total, l.TaxCode)), await TaxRegimeAsync(c, tx),
            invoice.CurrencyCode);
        if (kind == NoteKinds.Credit)
        {
            // Un céntimo de tolerancia: al devolver lo que queda de varias líneas el redondeo puede diferir del saldo.
            var minor = 1m / (decimal)Math.Pow(10, VortexApi.Suite.Money.Currencies.Get(invoice.CurrencyCode).Decimals);
            Require(breakdown.Total <= invoice.Creditable + minor, T("error.la_nota_supera_lo_que_queda", invoice.Creditable));
        }

        var id = Guid.NewGuid();
        var number = await DocumentNumbers.NextNoteAsync(c, tx, kind, invoice.Kind, Context.DeviceTag);
        var e = new InvoiceNoteIssued(id, number, invoice.Id, invoice.Number, invoice.CustomerId, reason, now, invoice.CurrencyCode,
            breakdown.Net, breakdown.Tax, breakdown.Total, lines, Clean(description), invoice.SaleId, invoice.OrderId);
        await InsertNoteAsync(c, tx, kind, e, "local", Context.UserName);
        await Outbox.EnqueueEventAsync(c, tx, now, kind == NoteKinds.Credit ? SuiteEvents.CreditNoteIssued : SuiteEvents.DebitNoteIssued, e,
            subjectType: "invoice", subjectId: invoice.Number, id: id);
        await AuditAsync(c, tx, now, AuditActions.Issued, "invoice", invoice.Id, number,
            $"{(kind == NoteKinds.Credit ? "Nota de crédito" : "Nota de débito")} ({reason}) de {invoice.Number} por {breakdown.Total:0.##} {invoice.CurrencyCode}");
        return number;
    }

    public async Task<IReadOnlyList<InvoiceNoteRow>> GetNotesAsync(Guid? invoiceId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT n.*, i.number AS invoice_number, p.name AS customer_name
            FROM invoice_notes n LEFT JOIN invoices i ON i.id = n.invoice_id LEFT JOIN parties p ON p.id = n.customer_id
            WHERE $invoice IS NULL OR n.invoice_id = $invoice ORDER BY n.issued_at DESC
            """, ReadNote, null, ("$invoice", invoiceId));
    }

    public async Task<IReadOnlyList<NoteLine>> GetNoteLinesAsync(Guid noteId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("SELECT * FROM invoice_note_lines WHERE note_id = $id ORDER BY line_no", r => new NoteLine(
            r.GuidOrNull("item_id"), r.Str("description"), r.Dec("quantity"), r.Dec("unit_price"), r.Dec("total"), r.StrOrNull("tax_code"),
            r.IsDBNull(r.GetOrdinal("invoice_line_no")) ? null : r.Int("invoice_line_no")), null, ("$id", noteId));
    }

    // ---------- Anulación ----------

    /// <summary>
    /// Anula un comprobante sin cobros ni notas (emitido por error). El pedido o la venta de caja quedan libres para emitir
    /// otro. Si ya tiene cobros o notas, se corrige con una nota de crédito.
    /// </summary>
    public Task VoidAsync(Guid invoiceId, string reason, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.VoidInvoice);
        Require(!string.IsNullOrWhiteSpace(reason), T("error.escribe_el_motivo_de_la_anulacion"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var invoice = await GetAsync(c, tx, invoiceId);
            Require(invoice is not null, T("error.el_comprobante_no_existe"));
            Require(!invoice!.IsVoided, T("error.el_comprobante_esta_anulado"));
            Require(invoice.CanVoid, T("error.tiene_cobros_o_notas_emite_nota"));
            var e = new InvoiceVoided(invoiceId, invoice.Number, reason.Trim(), invoice.SaleId, invoice.OrderId, invoice.CustomerId);
            await MarkVoidedAsync(c, tx, now, e);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.InvoiceVoided, e, subjectType: "invoice", subjectId: invoice.Number,
                id: DeterministicId.From("invoice_void", invoiceId));
            await AuditAsync(c, tx, now, AuditActions.Voided, "invoice", invoiceId, invoice.Number, reason.Trim());
            return 0;
        }, ct);
    }

    // ---------- Antigüedad de saldos y estado de cuenta ----------

    /// <summary>Saldos pendientes por cliente y moneda, repartidos por días de vencido (a la fecha de hoy).</summary>
    public async Task<IReadOnlyList<AgingRow>> GetAgingAsync(CancellationToken ct = default)
    {
        var today = DateOnly.ParseExact(Today(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await using var c = await Db.OpenAsync(ct);
        var open = await c.QueryAsync($"""
            {Select} WHERE i.status = 'issued' AND i.total + i.debited - i.credited - i.paid > 0
            """, Read);
        return open
            .GroupBy(i => (i.CustomerId, Name: i.CustomerName ?? T("info.clientes_varios"), i.CurrencyCode))
            .Select(g =>
            {
                decimal Bucket(int fromDays, int toDays) => g.Where(i => DaysOverdue(i, today) is var d && d >= fromDays && d <= toDays).Sum(i => i.Balance);
                // En moneda de la empresa, al tipo de cambio de cada comprobante (el de su emisión).
                decimal Base(IEnumerable<InvoiceRow> list) => list.Sum(i => i.Balance * (i.ExchangeRate ?? 1m));
                return new AgingRow(g.Key.CustomerId, g.Key.Name, g.Key.CurrencyCode, Bucket(int.MinValue, 0), Bucket(1, 30), Bucket(31, 60),
                    Bucket(61, 90), Bucket(91, int.MaxValue),
                    Base(g), Base(g.Where(i => DaysOverdue(i, today) > 0)));
            })
            .OrderByDescending(r => r.Overdue).ThenByDescending(r => r.Total)
            .ToList();
    }

    private static int DaysOverdue(InvoiceRow invoice, DateOnly today) => invoice.DueDate is { } due ? today.DayNumber - due.DayNumber : 0;

    /// <summary>
    /// Estado de cuenta de un cliente en una moneda: saldo anterior a <paramref name="from"/>, cargos (comprobantes y notas de
    /// débito) y abonos (cobros y notas de crédito) del periodo con el saldo acumulado, y lo vencido a hoy.
    /// </summary>
    public async Task<CustomerStatement> GetStatementAsync(Guid customerId, string currencyCode, DateOnly from, DateOnly to,
        CancellationToken ct = default)
    {
        Require(from <= to, T("error.la_fecha_inicial_no_puede"));
        await using var c = await Db.OpenAsync(ct);
        var customer = await c.FirstOrDefaultAsync("SELECT name, document_number FROM parties WHERE id = $id",
            r => new { Name = r.Str("name"), Document = r.StrOrNull("document_number") }, null, ("$id", customerId));
        Require(customer is not null, T("error.el_cliente_no_existe"));

        var movements = await c.QueryAsync("""
            SELECT issued_at AS at, 'invoice' AS kind, id, number, NULL AS reference, due_date, total AS charge, 0 AS credit
            FROM invoices WHERE customer_id = $customer AND currency_code = $currency AND status = 'issued'
            UNION ALL
            SELECT n.issued_at, CASE n.kind WHEN 'debit' THEN 'debit_note' ELSE 'credit_note' END, n.id, n.number, i.number, NULL,
                   CASE n.kind WHEN 'debit' THEN n.total ELSE 0 END, CASE n.kind WHEN 'debit' THEN 0 ELSE n.total END
            FROM invoice_notes n JOIN invoices i ON i.id = n.invoice_id
            WHERE n.customer_id = $customer AND n.currency_code = $currency AND i.status = 'issued'
            UNION ALL
            SELECT y.received_at, 'payment', y.id, y.number, i.number, NULL, 0, y.amount
            FROM payments y LEFT JOIN invoices i ON i.id = y.invoice_id
            WHERE y.customer_id = $customer AND y.currency_code = $currency
            ORDER BY 1, 2
            """, r => new
            {
                At = r.Time("at"), Kind = r.Str("kind"), Id = r.Guid("id"), Number = r.Str("number"), Reference = r.StrOrNull("reference"),
                Due = r.StrOrNull("due_date") is { } d ? DateOnly.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : (DateOnly?)null,
                Charge = r.Dec("charge"), Credit = r.Dec("credit"),
            }, null, ("$customer", customerId), ("$currency", currencyCode));

        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), ErpClock.Offset);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), ErpClock.Offset);
        var opening = movements.Where(m => m.At < start).Sum(m => m.Charge - m.Credit);
        var balance = opening;
        var entries = new List<StatementEntry>();
        foreach (var m in movements.Where(m => m.At >= start && m.At < end))
        {
            balance += m.Charge - m.Credit;
            entries.Add(new StatementEntry(m.At, m.Kind, m.Id, m.Number, m.Reference, m.Due, m.Charge, m.Credit, balance));
        }

        var today = DateOnly.ParseExact(Today(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var overdue = (await c.QueryAsync($"""
            {Select} WHERE i.customer_id = $customer AND i.currency_code = $currency AND i.status = 'issued'
              AND i.total + i.debited - i.credited - i.paid > 0
            """, Read, null, ("$customer", customerId), ("$currency", currencyCode)))
            .Where(i => i.IsOverdue(today)).Sum(i => i.Balance);
        return new CustomerStatement(customerId, customer!.Name, customer.Document, currencyCode, from, to, opening, entries, balance, overdue,
            Clock.GetUtcNow());
    }

    /// <summary>Monedas en que el cliente tiene movimientos (para elegir el estado de cuenta).</summary>
    public async Task<IReadOnlyList<string>> GetCustomerCurrenciesAsync(Guid customerId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT currency_code FROM invoices WHERE customer_id = $id UNION SELECT currency_code FROM payments WHERE customer_id = $id
            ORDER BY 1
            """, r => r.Str("currency_code"), null, ("$id", customerId));
    }

    // ---------- Consultas ----------

    public async Task<PagedResult<InvoiceRow>> GetInvoicesAsync(InvoiceQuery q, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE ($kind IS NULL OR i.kind = $kind)
              AND ($unpaid IS NULL OR (i.status = 'issued' AND i.total + i.debited - i.credited - i.paid > 0) = $unpaid)
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
        return await LinesAsync(c, null, invoiceId);
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
                r.StrOrNull("reference"), r.Time("received_at"), r.StrOrNull("user_name"), r.DecOrNull("paid_amount"),
                r.StrOrNull("paid_currency"), r.DecOrNull("exchange_rate"), r.DecOrNull("fx_difference")), null,
            ("$invoice", invoiceId), ("$limit", pageSize), ("$offset", (page - 1) * pageSize));
        return new PagedResult<PaymentRow>(rows, total, page, pageSize);
    }

    /// <summary>Cuentas por cobrar por cliente, la de más vencido primero.</summary>
    public async Task<IReadOnlyList<ReceivableRow>> GetReceivablesAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT i.customer_id, COALESCE(p.name, 'Clientes varios') AS customer_name, COUNT(*) AS invoices,
                   SUM(i.total + i.debited - i.credited - i.paid) AS balance,
                   SUM(CASE WHEN i.due_date < $today THEN i.total + i.debited - i.credited - i.paid ELSE 0 END) AS overdue,
                   MIN(i.due_date) AS oldest_due
            FROM invoices i LEFT JOIN parties p ON p.id = i.customer_id
            WHERE i.status = 'issued' AND i.total + i.debited - i.credited - i.paid > 0
            GROUP BY i.customer_id ORDER BY overdue DESC, balance DESC
            """, r => new ReceivableRow(r.GuidOrNull("customer_id"), r.Str("customer_name"), r.Int("invoices"), r.Dec("balance"),
                r.Dec("overdue"), r.StrOrNull("oldest_due") is { } d ? DateOnly.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : null),
            null, ("$today", Today()));
    }

    public async Task<PagedResult<PosSaleRow>> GetPosSalesAsync(bool uninvoicedOnly, int page, int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var where = $"WHERE ($only = 0 OR (s.invoice_id IS NULL AND s.status = 'completed')){SiteFilterOrNull("s.site_id")}";
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
        if (e.ShipmentId is { } shipment)
            await c.ExecAsync("UPDATE order_shipments SET invoice_id = $invoice WHERE id = $id", tx, ("$id", shipment), ("$invoice", e.InvoiceId));
        else if (e.OrderId is { } order)
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

    /// <summary>Nota emitida en otro equipo del ERP. Idempotente por Id de la nota.</summary>
    internal static async Task ApplyNoteIssuedAsync(SqliteConnection c, SqliteTransaction tx, string kind, InvoiceNoteIssued e)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM invoice_notes WHERE id = $id", tx, ("$id", e.NoteId)) > 0)
            return;
        await InsertNoteAsync(c, tx, kind, e, "remote", null);
    }

    /// <summary>Anulación hecha en otro equipo del ERP (Id de evento determinista: se aplica una vez).</summary>
    internal static Task ApplyInvoiceVoidedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, InvoiceVoided e) =>
        MarkVoidedAsync(c, tx, at, e);

    // ---------- Internos ----------

    private static async Task InsertNoteAsync(SqliteConnection c, SqliteTransaction tx, string kind, InvoiceNoteIssued e, string origin,
        string? userName)
    {
        await c.ExecAsync("""
            INSERT INTO invoice_notes (id, number, kind, origin, invoice_id, customer_id, reason, description, issued_at, currency_code,
                                       subtotal, tax, total, user_name)
            VALUES ($id, $number, $kind, $origin, $invoice, $customer, $reason, $desc, $at, $currency, $subtotal, $tax, $total, $user)
            """, tx, ("$id", e.NoteId), ("$number", e.Number), ("$kind", kind), ("$origin", origin), ("$invoice", e.InvoiceId),
            ("$customer", e.CustomerId), ("$reason", e.Reason), ("$desc", e.Description), ("$at", e.IssuedAt), ("$currency", e.CurrencyCode),
            ("$subtotal", e.Subtotal), ("$tax", e.Tax), ("$total", e.Total), ("$user", userName));
        var lineNo = 0;
        foreach (var l in e.Lines ?? [])
        {
            await c.ExecAsync("""
                INSERT INTO invoice_note_lines (note_id, line_no, item_id, description, quantity, unit_price, total, tax_code, invoice_line_no)
                VALUES ($id, $no, $item, $desc, $qty, $price, $total, $tax, $invoiceLine)
                """, tx, ("$id", e.NoteId), ("$no", ++lineNo), ("$item", l.ItemId), ("$desc", l.Description), ("$qty", l.Quantity),
                ("$price", l.UnitPrice), ("$total", l.Total), ("$tax", l.TaxCode), ("$invoiceLine", l.InvoiceLineNo));
            if (kind == NoteKinds.Credit && l.InvoiceLineNo is { } invoiceLine)
            {
                await c.ExecAsync("UPDATE invoice_lines SET credited_qty = credited_qty + $qty WHERE invoice_id = $invoice AND line_no = $no", tx,
                    ("$invoice", e.InvoiceId), ("$no", invoiceLine), ("$qty", l.Quantity));
            }
        }
        await c.ExecAsync(kind == NoteKinds.Credit
            ? "UPDATE invoices SET credited = credited + $total WHERE id = $id"
            : "UPDATE invoices SET debited = debited + $total WHERE id = $id", tx, ("$id", e.InvoiceId), ("$total", e.Total));
    }

    /// <summary>Marca el comprobante como anulado y libera su pedido o venta de caja para emitir otro.</summary>
    private static async Task MarkVoidedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, InvoiceVoided e)
    {
        await c.ExecAsync("""
            UPDATE invoices SET status = 'voided', voided_at = $at, void_reason = $reason WHERE id = $id AND status = 'issued'
            """, tx, ("$id", e.InvoiceId), ("$at", at), ("$reason", e.Reason));
        await c.ExecAsync("UPDATE sales_orders SET invoice_id = NULL WHERE invoice_id = $id", tx, ("$id", e.InvoiceId));
        await c.ExecAsync("UPDATE order_shipments SET invoice_id = NULL WHERE invoice_id = $id", tx, ("$id", e.InvoiceId));
        await c.ExecAsync("UPDATE pos_sales SET invoice_id = NULL WHERE invoice_id = $id", tx, ("$id", e.InvoiceId));
    }

    private static InvoiceNoteRow ReadNote(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("number"), r.Str("kind"), r.Str("origin"), r.Guid("invoice_id"), r.StrOrNull("invoice_number"),
        r.GuidOrNull("customer_id"), r.StrOrNull("customer_name"), r.Str("reason"), r.StrOrNull("description"), r.Time("issued_at"),
        r.Str("currency_code"), r.Dec("subtotal"), r.Dec("tax"), r.Dec("total"), r.StrOrNull("user_name"));

    private static async Task InsertInvoiceAsync(SqliteConnection c, SqliteTransaction tx, InvoiceIssued e, string origin, string? userName)
    {
        await c.ExecAsync("""
            INSERT INTO invoices (id, number, kind, origin, customer_id, order_id, sale_id, issued_at, due_date, currency_code, subtotal,
                                  tax, total, paid, user_name, exchange_rate, shipment_id)
            VALUES ($id, $number, $kind, $origin, $customer, $order, $sale, $at, $due, $currency, $subtotal, $tax, $total, $paid, $user, $rate,
                    $shipment)
            """, tx, ("$id", e.InvoiceId), ("$number", e.Number), ("$kind", e.Kind ?? InvoiceKinds.Invoice), ("$origin", origin),
            ("$customer", e.CustomerId), ("$order", e.OrderId), ("$sale", e.SaleId), ("$at", e.IssuedAt),
            ("$due", e.DueDate?.ToString("yyyy-MM-dd")), ("$currency", e.CurrencyCode), ("$subtotal", e.Subtotal), ("$tax", e.Tax),
            ("$total", e.Total), ("$paid", e.Paid), ("$user", userName), ("$rate", e.ExchangeRate), ("$shipment", e.ShipmentId));
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
            INSERT INTO payments (id, number, customer_id, invoice_id, sale_id, amount, currency_code, method, reference, received_at, user_name,
                                  paid_amount, paid_currency, exchange_rate, fx_difference)
            VALUES ($id, $number, $customer, $invoice, $sale, $amount, $currency, $method, $ref, $at, $user, $paid, $paidCurrency, $rate, $fx)
            """, tx, ("$id", e.PaymentId), ("$number", e.Number ?? e.PaymentId.ToString()[..8].ToUpperInvariant()), ("$customer", e.CustomerId),
            ("$invoice", e.InvoiceId), ("$sale", e.SaleId), ("$amount", e.Amount), ("$currency", e.CurrencyCode), ("$method", e.Method),
            ("$ref", e.Reference), ("$at", at), ("$user", userName), ("$paid", e.PaidAmount), ("$paidCurrency", e.PaidCurrency),
            ("$rate", e.ExchangeRate), ("$fx", e.FxDifference));
        if (e.InvoiceId is { } invoice)
            // Tope: lo que vale el comprobante con sus notas (un cobro repetido de otro equipo no lo pasa). Nunca baja lo cobrado.
            await c.ExecAsync("UPDATE invoices SET paid = MAX(paid, MIN(total + debited - credited, paid + $amount)) WHERE id = $id", tx,
                ("$id", invoice), ("$amount", e.Amount));
    }

    /// <summary>Categoría de impuesto de cada artículo (la del hub; sin dato, tasa general).</summary>
    private static async Task<Dictionary<Guid, string>> TaxCodesAsync(SqliteConnection c, SqliteTransaction tx, IEnumerable<Guid> items)
    {
        var codes = new Dictionary<Guid, string>();
        foreach (var id in items.Distinct())
            codes[id] = TaxCategories.Normalize(await c.ScalarAsync<string?>("SELECT tax_code FROM items WHERE id = $id", tx, ("$id", id)));
        return codes;
    }


    /// <summary>Plazo de pago del cliente (de sus condiciones del hub) o el habitual si no tiene.</summary>
    internal static async Task<int> TermDaysAsync(SqliteConnection c, SqliteTransaction? tx, Guid? customerId) =>
        customerId is { } id && await c.ScalarAsync<long?>("SELECT payment_term_days FROM parties WHERE id = $id", tx, ("$id", id)) is { } days
            ? (int)days
            : DefaultCreditDays;

    /// <summary>Plazo de pago del cliente, para proponerlo al facturar un pedido.</summary>
    public async Task<int> GetTermDaysAsync(Guid? customerId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await TermDaysAsync(c, null, customerId);
    }

    private string Today() => DateOnly.FromDateTime(Clock.GetUtcNow().ToOffset(ErpClock.Offset).DateTime).ToString("yyyy-MM-dd");

    // ---------- Cobranza ----------

    /// <summary>
    /// Recordatorio de cobranza del cliente en una moneda: sus comprobantes vencidos y un mensaje listo para enviar por
    /// WhatsApp o correo (con número, fecha de vencimiento y saldo de cada uno). Null si no tiene nada vencido.
    /// </summary>
    public async Task<CollectionReminder?> GetReminderAsync(Guid customerId, string currency, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var today = DateOnly.ParseExact(Today(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var overdue = (await c.QueryAsync($"""
            {Select} WHERE i.customer_id = $customer AND i.currency_code = $currency AND i.status = 'issued'
                       AND i.total + i.debited - i.credited - i.paid > 0 AND i.due_date < $today
            ORDER BY i.due_date
            """, Read, null, ("$customer", customerId), ("$currency", currency), ("$today", Today()))).ToList();
        if (overdue.Count == 0)
            return null;
        var party = await c.FirstOrDefaultAsync("SELECT name, phone, email FROM parties WHERE id = $id",
            r => new { Name = r.Str("name"), Phone = r.StrOrNull("phone"), Email = r.StrOrNull("email") }, null, ("$id", customerId));
        var company = await c.ScalarAsync<string?>("SELECT COALESCE(trade_name, legal_name) FROM company WHERE id = 1");
        var money = VortexApi.Suite.Money.Currencies.Get(currency);
        var culture = System.Globalization.CultureInfo.GetCultureInfo("es-PE");
        var total = overdue.Sum(i => i.Balance);
        var detail = string.Join("\n", overdue.Select(i => T("cobranza.linea", i.Number, i.DueDate!.Value.ToString("dd/MM/yyyy", culture),
            money.Format(i.Balance, culture), today.DayNumber - i.DueDate.Value.DayNumber)));
        var message = T("cobranza.mensaje", party?.Name ?? "", money.Format(total, culture), detail, company ?? "");
        var last = await c.ScalarAsync<string?>("SELECT MAX(sent_at) FROM collection_reminders WHERE customer_id = $id", null, ("$id", customerId));
        return new CollectionReminder(customerId, party?.Name ?? "?", party?.Phone, party?.Email, currency, total, overdue, message,
            last is null ? null : DateTimeOffset.Parse(last, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Deja constancia de que se envió el recordatorio (canal, monto vencido y quién) y lo audita.</summary>
    public Task RecordReminderAsync(Guid customerId, string channel, decimal amount, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.RegisterPayment);
        Require(channel is ReminderChannels.WhatsApp or ReminderChannels.Email, T("error.canal_invalido"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await c.ExecAsync("""
                INSERT INTO collection_reminders (id, customer_id, channel, sent_at, amount, user_name) VALUES ($id, $customer, $channel, $at, $amount, $user)
                """, tx, ("$id", Guid.NewGuid()), ("$customer", customerId), ("$channel", channel), ("$at", now), ("$amount", amount),
                ("$user", Context.UserName));
            await AuditAsync(c, tx, now, "collection.reminded", "party", customerId, null, $"Recordatorio por {channel}: {amount:0.00} vencido");
            return 0;
        }, ct);
    }

    /// <summary>Último recordatorio de cobranza por cliente.</summary>
    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastRemindersAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return (await c.QueryAsync("SELECT customer_id, MAX(sent_at) AS last FROM collection_reminders GROUP BY customer_id",
            r => (Id: r.Guid("customer_id"), At: r.Time("last")))).ToDictionary(x => x.Id, x => x.At);
    }

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
        r.Str("currency_code"), r.Dec("subtotal"), r.Dec("tax"), r.Dec("total"), r.Dec("paid"), r.StrOrNull("user_name"),
        r.Str("status"), r.Dec("credited"), r.Dec("debited"), r.TimeOrNull("voided_at"), r.StrOrNull("void_reason"),
        r.DecOrNull("exchange_rate"));

    internal static Task<List<InvoiceLineRow>> LinesAsync(SqliteConnection c, SqliteTransaction? tx, Guid invoiceId) =>
        c.QueryAsync("SELECT * FROM invoice_lines WHERE invoice_id = $id ORDER BY line_no", r => new InvoiceLineRow(
            r.Int("line_no"), r.GuidOrNull("item_id"), r.Str("description"), r.Dec("quantity"), r.Dec("unit_price"), r.Dec("total"),
            r.StrOrNull("tax_code"), r.Dec("credited_qty")), tx, ("$id", invoiceId));

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
