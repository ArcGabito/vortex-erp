using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Sync;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Services;

/// <summary>
/// Compras y cuentas por pagar (fase 4a): solicitudes de compra con aprobación por monto, facturas de proveedor con la
/// conciliación de tres vías (orden, recepción y factura), cuentas por pagar con vencimientos y pagos a proveedores. Son
/// registros del ERP (<c>erp.record.upserted</c>): los ve cada equipo del ERP y gana el cambio más reciente. Los pagos son
/// registros aparte, así dos equipos que pagan la misma factura sin conexión suman los dos pagos (no se pisan).
/// </summary>
public sealed class PayablesService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    private const string SettingsKey = "purchase";

    // ---------- Ajustes ----------

    public async Task<PurchaseSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await SettingsAsync(c, null);
    }

    public Task SaveSettingsAsync(PurchaseSettings settings, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ApprovePurchase);
        Require(settings.ApprovalThreshold is null or >= 0, T("error.umbral_invalido"));
        Require(settings.PriceTolerancePercent is >= 0 and <= 50, T("error.tolerancia_invalida"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await PublishAsync(c, tx, now, ErpRecordKinds.PurchaseSettings, SettingsKey, settings);
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.SettingsChanged, "purchase_settings", null, null,
                $"Aprobación desde {settings.ApprovalThreshold?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—"} · tolerancia {settings.PriceTolerancePercent:0.##} %");
            return 0;
        }, ct);
    }

    internal static async Task<PurchaseSettings> SettingsAsync(SqliteConnection c, SqliteTransaction? tx) =>
        await c.ScalarAsync<string?>("SELECT value FROM erp_settings WHERE key = $key", tx, ("$key", SettingsKey)) is { } json
            ? JsonSerializer.Deserialize<PurchaseSettings>(json, VortexJson.Options) ?? PurchaseSettings.Default
            : PurchaseSettings.Default;

    /// <summary>Si una compra por ese monto necesita aprobación (supera el umbral, en la moneda de la empresa).</summary>
    internal static async Task<bool> NeedsApprovalAsync(SqliteConnection c, SqliteTransaction tx, decimal total, string currency, DateOnly today) =>
        (await SettingsAsync(c, tx)).ApprovalThreshold is { } threshold && await Credit.ToCompanyAsync(c, tx, total, currency, today) > threshold;

    // ---------- Solicitudes de compra ----------

    public async Task<IReadOnlyList<PurchaseRequestRow>> GetRequestsAsync(string? status = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var rows = await c.QueryAsync("""
            SELECT r.*, s.name AS site_name, p.name AS supplier_name, o.number AS po_number
            FROM purchase_requests r LEFT JOIN sites s ON s.id = r.site_id LEFT JOIN parties p ON p.id = r.supplier_id
                 LEFT JOIN purchase_orders o ON o.id = r.po_id
            WHERE ($status IS NULL OR r.status = $status) ORDER BY r.requested_at DESC LIMIT 200
            """, r => new PurchaseRequestRow(r.Guid("id"), r.Str("number"), r.GuidOrNull("site_id"), r.StrOrNull("site_name"),
                r.GuidOrNull("supplier_id"), r.StrOrNull("supplier_name"), r.Str("status"), r.Str("currency_code"), r.Dec("total"),
                r.StrOrNull("notes"), r.StrOrNull("requested_by"), r.Time("requested_at"), r.StrOrNull("decided_by"), r.TimeOrNull("decided_at"),
                r.StrOrNull("reason"), r.GuidOrNull("po_id"), r.StrOrNull("po_number"), []), null, ("$status", Clean(status)));
        for (var i = 0; i < rows.Count; i++)
            rows[i] = rows[i] with
            {
                Lines = await c.QueryAsync("""
                    SELECT l.line_no, l.item_id, COALESCE(i.sku, '?') AS sku, COALESCE(i.name, '?') AS item_name, l.quantity, COALESCE(l.unit_cost, 0) AS unit_cost
                    FROM purchase_request_lines l LEFT JOIN items i ON i.id = l.item_id WHERE l.request_id = $id ORDER BY l.line_no
                    """, r => new PurchaseLineRow(r.Int("line_no"), r.Guid("item_id"), r.Str("sku"), r.Str("item_name"), r.Dec("quantity"),
                        r.Dec("unit_cost"), 0), null, ("$id", rows[i].Id)),
            };
        return rows;
    }

    /// <summary>
    /// Pide una compra. Si supera el umbral de aprobación queda por aprobar (salvo que quien la pide pueda aprobar); si no,
    /// queda aprobada y lista para convertirse en orden.
    /// </summary>
    public Task<string> CreateRequestAsync(Guid? siteId, Guid? supplierId, IReadOnlyList<PurchaseRequestLine> lines, string currencyCode,
        string? notes = null, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.RequestPurchase);
        Require(lines.Count > 0, T("error.agrega_al_menos_un_articulo"));
        Require(lines.All(l => l.Quantity > 0), T("error.las_cantidades_deben_ser_mayores"));
        Require(lines.All(l => l.UnitCost is null or >= 0), T("error.indica_el_costo_unitario_de"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var site = siteId ?? await DefaultSiteAsync(c, tx);
            Require(site is null || CanUseSite(site.Value), T("error.sede_no_asignada"));
            var currency = currencyCode.Trim().ToUpperInvariant();
            var total = lines.Sum(l => Tax.Round(l.Quantity * (l.UnitCost ?? 0)));
            var pending = await NeedsApprovalAsync(c, tx, total, currency, Today(now)) && !Context.Can(ErpPermissions.ApprovePurchase);
            var data = new PurchaseRequestData(Guid.NewGuid(), await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.PurchaseRequest, Context.DeviceTag),
                site, supplierId, pending ? PurchaseRequestStatuses.Pending : PurchaseRequestStatuses.Approved, currency, total, Clean(notes),
                Context.UserName, now, pending ? null : Context.UserName, pending ? null : now, null, null, lines.ToList());
            await PublishAsync(c, tx, now, ErpRecordKinds.PurchaseRequest, data.Id.ToString(), data);
            return data.Number;
        }, ct);
    }

    public Task ApproveRequestAsync(Guid id, CancellationToken ct = default) => DecideAsync(id, true, null, ct);

    public Task RejectRequestAsync(Guid id, string reason, CancellationToken ct = default)
    {
        Require(!string.IsNullOrWhiteSpace(reason), T("error.indica_el_motivo"));
        return DecideAsync(id, false, reason, ct);
    }

    private Task DecideAsync(Guid id, bool approve, string? reason, CancellationToken ct)
    {
        RequirePermission(ErpPermissions.ApprovePurchase);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var request = await RequestAsync(c, tx, id);
            Require(request.Status == PurchaseRequestStatuses.Pending, T("error.la_solicitud_no_esta_por_aprobar"));
            var updated = request with
            {
                Status = approve ? PurchaseRequestStatuses.Approved : PurchaseRequestStatuses.Rejected, DecidedBy = Context.UserName,
                DecidedAt = now, Reason = Clean(reason),
            };
            await PublishAsync(c, tx, now, ErpRecordKinds.PurchaseRequest, id.ToString(), updated);
            await AuditAsync(c, tx, now, approve ? "purchase_request.approved" : "purchase_request.rejected", "purchase_request", id,
                request.Number, Clean(reason) ?? $"{request.Total:0.##} {request.CurrencyCode}");
            return 0;
        }, ct);
    }

    /// <summary>Convierte una solicitud aprobada en orden de compra al proveedor elegido (con los costos de la solicitud).</summary>
    public Task<string> OrderRequestAsync(Guid id, Guid supplierId, IReadOnlyList<PurchaseRequestLine>? costs = null, DateOnly? expectedAt = null,
        CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.CreatePurchase);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var request = await RequestAsync(c, tx, id);
            Require(request.Status == PurchaseRequestStatuses.Approved, T("error.la_solicitud_no_esta_aprobada"));
            var lines = request.Lines.Select(l => new DocumentLine(l.ItemId, l.Quantity,
                costs?.FirstOrDefault(x => x.ItemId == l.ItemId)?.UnitCost ?? l.UnitCost)).ToList();
            var (poId, number) = await PurchaseService.CreateInternalAsync(c, tx, now, Context, supplierId, request.SiteId, lines,
                request.CurrencyCode, expectedAt, id);
            await PublishAsync(c, tx, now, ErpRecordKinds.PurchaseRequest, id.ToString(),
                request with { Status = PurchaseRequestStatuses.Ordered, SupplierId = supplierId, PurchaseOrderId = poId });
            return number;
        }, ct);
    }

    private static async Task<PurchaseRequestData> RequestAsync(SqliteConnection c, SqliteTransaction tx, Guid id)
    {
        var row = await c.FirstOrDefaultAsync("SELECT * FROM purchase_requests WHERE id = $id", r => new
        {
            Number = r.Str("number"), Site = r.GuidOrNull("site_id"), Supplier = r.GuidOrNull("supplier_id"), Status = r.Str("status"),
            Currency = r.Str("currency_code"), Total = r.Dec("total"), Notes = r.StrOrNull("notes"), By = r.StrOrNull("requested_by"),
            At = r.Time("requested_at"), DecidedBy = r.StrOrNull("decided_by"), DecidedAt = r.TimeOrNull("decided_at"),
            Reason = r.StrOrNull("reason"), Po = r.GuidOrNull("po_id"),
        }, tx, ("$id", id));
        Require(row is not null, T("error.la_solicitud_no_existe"));
        var lines = await c.QueryAsync("SELECT item_id, quantity, unit_cost FROM purchase_request_lines WHERE request_id = $id ORDER BY line_no",
            r => new PurchaseRequestLine(r.Guid("item_id"), r.Dec("quantity"), r.DecOrNull("unit_cost")), tx, ("$id", id));
        return new PurchaseRequestData(id, row!.Number, row.Site, row.Supplier, row.Status, row.Currency, row.Total, row.Notes, row.By, row.At,
            row.DecidedBy, row.DecidedAt, row.Reason, row.Po, lines);
    }

    // ---------- Facturas de proveedor ----------

    /// <summary>
    /// Registra la factura de un proveedor. Con orden de compra se concilia en tres vías: cada artículo debe estar en la
    /// orden, lo facturado (con las facturas anteriores) no puede pasar de lo recibido y el costo no puede diferir del de
    /// la orden más que la tolerancia. Si algo no cuadra queda retenida (no se paga) con las diferencias anotadas.
    /// </summary>
    public Task<Guid> RegisterInvoiceAsync(SupplierInvoiceDraft d, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.PaySuppliers);
        Require(!string.IsNullOrWhiteSpace(d.DocumentNumber), T("error.indica_el_numero_de_la_factura"));
        Require(d.Lines.Count > 0, T("error.agrega_al_menos_un_articulo"));
        Require(d.Lines.All(l => l.Quantity > 0 && l.UnitCost >= 0), T("error.indica_el_costo_unitario_de"));
        Require(d.DueOn is null || d.DueOn >= d.IssuedOn, T("error.el_vencimiento_es_anterior"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var supplier = await c.FirstOrDefaultAsync("SELECT name, payment_term_days FROM parties WHERE id = $id AND is_deleted = 0",
                r => new { Name = r.Str("name"), Days = r.DecOrNull("payment_term_days") }, tx, ("$id", d.SupplierId));
            Require(supplier is not null, T("error.el_proveedor_no_existe"));
            var number = d.DocumentNumber.Trim().ToUpperInvariant();
            Require(await c.ScalarAsync<long>("""
                SELECT COUNT(*) FROM supplier_invoices WHERE supplier_id = $s AND document_number = $n AND status <> 'void'
                """, tx, ("$s", d.SupplierId), ("$n", number)) == 0, T("error.factura_de_proveedor_repetida", number));

            var regime = await TaxRegimeAsync(c, tx);
            var money = VortexApi.Suite.Money.Currencies.Get(d.CurrencyCode);
            var subtotal = 0m;
            var tax = 0m;
            foreach (var l in d.Lines)
            {
                var taxCode = await c.ScalarAsync<string?>("SELECT tax_code FROM items WHERE id = $id", tx, ("$id", l.ItemId));
                var amount = money.Round(l.Quantity * l.UnitCost);
                subtotal += amount;
                tax += money.Round(amount * regime.RateOf(taxCode));
            }

            var notes = d.PurchaseOrderId is { } po ? await MatchAsync(c, tx, po, d) : [];
            var data = new SupplierInvoiceData(Guid.NewGuid(), d.SupplierId, d.PurchaseOrderId, number, d.IssuedOn,
                d.DueOn ?? d.IssuedOn.AddDays((int)(supplier!.Days ?? 0)), d.CurrencyCode.Trim().ToUpperInvariant(), subtotal, tax, subtotal + tax,
                notes.Count > 0 ? SupplierInvoiceStatuses.Held : SupplierInvoiceStatuses.Approved, notes.Count > 0 ? string.Join("\n", notes) : null,
                null, null, null, null, now, Context.UserName, d.Lines.ToList());
            await PublishAsync(c, tx, now, ErpRecordKinds.SupplierInvoice, data.Id.ToString(), data);
            if (notes.Count > 0)
                await AuditAsync(c, tx, now, "supplier_invoice.held", "supplier_invoice", data.Id, number, string.Join("; ", notes));
            return data.Id;
        }, ct);
    }

    /// <summary>Diferencias entre la factura y la orden (y lo recibido). Vacío: cuadra.</summary>
    private static async Task<List<string>> MatchAsync(SqliteConnection c, SqliteTransaction tx, Guid poId, SupplierInvoiceDraft d)
    {
        var order = await c.FirstOrDefaultAsync("SELECT number, supplier_id, currency_code, status FROM purchase_orders WHERE id = $id",
            r => new { Number = r.Str("number"), Supplier = r.Guid("supplier_id"), Currency = r.Str("currency_code"), Status = r.Str("status") },
            tx, ("$id", poId));
        Require(order is not null, T("error.la_orden_de_compra_no_existe"));
        Require(order!.Supplier == d.SupplierId, T("error.la_orden_es_de_otro_proveedor"));
        Require(string.Equals(order.Currency, d.CurrencyCode, StringComparison.OrdinalIgnoreCase), T("error.la_orden_es_en_otra_moneda", order.Currency));
        var tolerance = (await SettingsAsync(c, tx)).PriceTolerancePercent / 100m;
        var notes = new List<string>();
        foreach (var item in d.Lines.GroupBy(l => l.ItemId))
        {
            var name = await c.ScalarAsync<string?>("SELECT name FROM items WHERE id = $id", tx, ("$id", item.Key)) ?? "?";
            var po = await c.QueryAsync("SELECT quantity, unit_cost, received_qty FROM purchase_order_lines WHERE po_id = $po AND item_id = $item",
                r => (Qty: r.Dec("quantity"), Cost: r.Dec("unit_cost"), Received: r.Dec("received_qty")), tx, ("$po", poId), ("$item", item.Key));
            if (po.Count == 0)
            {
                notes.Add(T("tresvias.no_esta_en_la_orden", name, order.Number));
                continue;
            }
            var invoicedBefore = await c.ScalarAsync<decimal>("""
                SELECT COALESCE(SUM(l.quantity), 0) FROM supplier_invoice_lines l JOIN supplier_invoices i ON i.id = l.invoice_id
                WHERE i.po_id = $po AND i.status <> 'void' AND l.item_id = $item
                """, tx, ("$po", poId), ("$item", item.Key));
            var received = po.Sum(p => p.Received);
            var invoiced = invoicedBefore + item.Sum(l => l.Quantity);
            if (invoiced > received)
                notes.Add(T("tresvias.mas_de_lo_recibido", name, Qty(invoiced), Qty(received)));
            var orderCost = po.Sum(p => p.Qty * p.Cost) / Math.Max(po.Sum(p => p.Qty), 1);
            foreach (var cost in item.Select(l => l.UnitCost).Distinct())
                if (orderCost > 0 ? Math.Abs(cost - orderCost) / orderCost > tolerance : cost > 0)
                    notes.Add(T("tresvias.costo_distinto", name, cost.ToString("0.00##", CultureInfo.InvariantCulture),
                        orderCost.ToString("0.00##", CultureInfo.InvariantCulture)));
        }
        return notes;
    }

    private static string Qty(decimal q) => q.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Libera una factura retenida (con el motivo): desde ahí se puede pagar.</summary>
    public Task ReleaseInvoiceAsync(Guid id, string reason, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ApprovePurchase);
        Require(!string.IsNullOrWhiteSpace(reason), T("error.indica_el_motivo"));
        return ChangeInvoiceAsync(id, inv =>
        {
            Require(inv.Status == SupplierInvoiceStatuses.Held, T("error.la_factura_no_esta_retenida"));
            return inv with { Status = SupplierInvoiceStatuses.Approved, ReleasedBy = Context.UserName, ReleasedAt = Clock.GetUtcNow(), ReleaseReason = reason.Trim() };
        }, "supplier_invoice.released", reason.Trim(), ct);
    }

    /// <summary>Anula una factura de proveedor sin pagos (p. ej. se registró mal).</summary>
    public Task VoidInvoiceAsync(Guid id, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.PaySuppliers);
        return InTransactionAsync(async (c, tx, now) =>
        {
            Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM supplier_payments WHERE invoice_id = $id", tx, ("$id", id)) == 0,
                T("error.la_factura_tiene_pagos"));
            var inv = await InvoiceAsync(c, tx, id);
            Require(inv.Status != SupplierInvoiceStatuses.Void, T("error.la_factura_ya_esta_anulada"));
            await PublishAsync(c, tx, now, ErpRecordKinds.SupplierInvoice, id.ToString(), inv with { Status = SupplierInvoiceStatuses.Void });
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.Cancelled, "supplier_invoice", id, inv.DocumentNumber, null);
            return 0;
        }, ct);
    }

    /// <summary>Programa (o deja sin programar) la fecha en que se pagará.</summary>
    public Task PlanPaymentAsync(Guid id, DateOnly? on, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.PaySuppliers);
        return ChangeInvoiceAsync(id, inv => inv with { PlannedOn = on }, null, null, ct);
    }

    private Task ChangeInvoiceAsync(Guid id, Func<SupplierInvoiceData, SupplierInvoiceData> change, string? action, string? detail, CancellationToken ct) =>
        InTransactionAsync(async (c, tx, now) =>
        {
            var inv = await InvoiceAsync(c, tx, id);
            var updated = change(inv);
            await PublishAsync(c, tx, now, ErpRecordKinds.SupplierInvoice, id.ToString(), updated);
            if (action is not null)
                await AuditAsync(c, tx, now, action, "supplier_invoice", id, inv.DocumentNumber, detail);
            return 0;
        }, ct);

    /// <summary>Paga (todo o una parte) una factura aprobada.</summary>
    public Task<Guid> RegisterPaymentAsync(Guid invoiceId, decimal amount, DateOnly paidOn, string method, string? reference = null,
        CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.PaySuppliers);
        Require(amount > 0, T("error.el_monto_debe_ser_mayor"));
        Require(PaymentMethods.All.Contains(method), T("error.medio_de_pago_desconocido"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var inv = await InvoiceAsync(c, tx, invoiceId);
            Require(inv.Status != SupplierInvoiceStatuses.Held, T("error.la_factura_esta_retenida"));
            Require(inv.Status == SupplierInvoiceStatuses.Approved, T("error.la_factura_ya_esta_anulada"));
            var paid = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM supplier_payments WHERE invoice_id = $id", tx, ("$id", invoiceId));
            Require(amount <= inv.Total - paid, T("error.el_pago_supera_el_saldo", (inv.Total - paid).ToString("0.00", CultureInfo.InvariantCulture)));
            var data = new SupplierPaymentData(Guid.NewGuid(), invoiceId, amount, paidOn, method, Clean(reference), now, Context.UserName);
            await PublishAsync(c, tx, now, ErpRecordKinds.SupplierPayment, data.Id.ToString(), data);
            if (inv.PlannedOn is not null && amount >= inv.Total - paid)
                await PublishAsync(c, tx, now, ErpRecordKinds.SupplierInvoice, invoiceId.ToString(), inv with { PlannedOn = null });
            await AuditAsync(c, tx, now, "supplier_payment.registered", "supplier_invoice", invoiceId, inv.DocumentNumber,
                $"{amount:0.00} {inv.CurrencyCode} · {method}");
            return data.Id;
        }, ct);
    }

    public async Task<IReadOnlyList<SupplierInvoiceRow>> GetInvoicesAsync(string? filter = null, Guid? supplierId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var today = Today(Clock.GetUtcNow()).ToString("yyyy-MM-dd");
        return await c.QueryAsync("""
            SELECT i.*, p.name AS supplier_name, o.number AS po_number,
                   (SELECT COALESCE(SUM(amount), 0) FROM supplier_payments x WHERE x.invoice_id = i.id) AS paid
            FROM supplier_invoices i LEFT JOIN parties p ON p.id = i.supplier_id LEFT JOIN purchase_orders o ON o.id = i.po_id
            WHERE ($supplier IS NULL OR i.supplier_id = $supplier)
              AND ($filter IS NULL
                   OR ($filter = 'open' AND i.status <> 'void' AND i.total > (SELECT COALESCE(SUM(amount), 0) FROM supplier_payments x WHERE x.invoice_id = i.id))
                   OR ($filter = 'held' AND i.status = 'held')
                   OR ($filter = 'overdue' AND i.status = 'approved' AND i.due_on < $today
                       AND i.total > (SELECT COALESCE(SUM(amount), 0) FROM supplier_payments x WHERE x.invoice_id = i.id)))
            ORDER BY CASE WHEN i.status = 'held' THEN 0 ELSE 1 END, i.due_on, i.created_at DESC
            LIMIT 300
            """, ReadInvoice, null, ("$supplier", supplierId), ("$filter", Clean(filter)), ("$today", today));
    }

    public async Task<IReadOnlyList<SupplierPaymentRow>> GetPaymentsAsync(Guid invoiceId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("SELECT * FROM supplier_payments WHERE invoice_id = $id ORDER BY paid_on, created_at",
            r => new SupplierPaymentRow(r.Guid("id"), r.Guid("invoice_id"), r.Dec("amount"), Date(r.Str("paid_on")), r.Str("method"),
                r.StrOrNull("reference"), r.Time("created_at"), r.StrOrNull("user_name")), null, ("$id", invoiceId));
    }

    /// <summary>Cuentas por pagar por proveedor y moneda, en tramos de vencimiento (las retenidas también suman).</summary>
    public async Task<IReadOnlyList<PayableSummary>> GetPayablesAsync(CancellationToken ct = default)
    {
        var today = Today(Clock.GetUtcNow());
        var open = await GetInvoicesAsync("open", null, ct);
        return open.GroupBy(i => (i.SupplierId, i.CurrencyCode)).Select(g =>
        {
            decimal Sum(Func<int, bool> days) => g.Where(i => days(today.DayNumber - i.DueOn.DayNumber)).Sum(i => i.Balance);
            return new PayableSummary(g.Key.SupplierId, g.First().SupplierName ?? "?", g.Key.CurrencyCode, Sum(d => d <= 0), Sum(d => d is > 0 and <= 30),
                Sum(d => d is > 30 and <= 60), Sum(d => d > 60), g.Count(), g.Count(i => i.IsHeld));
        }).OrderByDescending(s => s.Total).ToList();
    }

    private static async Task<SupplierInvoiceData> InvoiceAsync(SqliteConnection c, SqliteTransaction tx, Guid id)
    {
        var inv = await c.FirstOrDefaultAsync("SELECT * FROM supplier_invoices WHERE id = $id", r => new SupplierInvoiceData(
            r.Guid("id"), r.Guid("supplier_id"), r.GuidOrNull("po_id"), r.Str("document_number"), Date(r.Str("issued_on")), Date(r.Str("due_on")),
            r.Str("currency_code"), r.Dec("subtotal"), r.Dec("tax"), r.Dec("total"), r.Str("status"), r.StrOrNull("match_note"),
            r.StrOrNull("released_by"), r.TimeOrNull("released_at"), r.StrOrNull("release_reason"),
            r.StrOrNull("planned_on") is { } p ? Date(p) : null, r.Time("created_at"), r.StrOrNull("user_name"), []), tx, ("$id", id));
        Require(inv is not null, T("error.la_factura_no_existe"));
        var lines = await c.QueryAsync("SELECT item_id, quantity, unit_cost FROM supplier_invoice_lines WHERE invoice_id = $id ORDER BY line_no",
            r => new SupplierInvoiceLine(r.Guid("item_id"), r.Dec("quantity"), r.Dec("unit_cost")), tx, ("$id", id));
        return inv! with { Lines = lines };
    }

    private static SupplierInvoiceRow ReadInvoice(SqliteDataReader r) => new(
        r.Guid("id"), r.Guid("supplier_id"), r.StrOrNull("supplier_name"), r.GuidOrNull("po_id"), r.StrOrNull("po_number"), r.Str("document_number"),
        Date(r.Str("issued_on")), Date(r.Str("due_on")), r.Str("currency_code"), r.Dec("subtotal"), r.Dec("tax"), r.Dec("total"), r.Dec("paid"),
        r.Str("status"), r.StrOrNull("match_note"), r.StrOrNull("released_by"), r.StrOrNull("release_reason"),
        r.StrOrNull("planned_on") is { } p ? Date(p) : null, r.Time("created_at"));

    private static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime);

    // ---------- Registros del ERP (erp.record.upserted) ----------

    public sealed record PurchaseRequestData(
        Guid Id, string Number, Guid? SiteId, Guid? SupplierId, string Status, string CurrencyCode, decimal Total, string? Notes, string? RequestedBy,
        DateTimeOffset RequestedAt, string? DecidedBy, DateTimeOffset? DecidedAt, string? Reason, Guid? PurchaseOrderId,
        IReadOnlyList<PurchaseRequestLine> Lines);

    public sealed record SupplierInvoiceData(
        Guid Id, Guid SupplierId, Guid? PurchaseOrderId, string DocumentNumber, DateOnly IssuedOn, DateOnly DueOn, string CurrencyCode,
        decimal Subtotal, decimal Tax, decimal Total, string Status, string? MatchNote, string? ReleasedBy, DateTimeOffset? ReleasedAt,
        string? ReleaseReason, DateOnly? PlannedOn, DateTimeOffset CreatedAt, string? UserName, IReadOnlyList<SupplierInvoiceLine> Lines);

    public sealed record SupplierPaymentData(Guid Id, Guid InvoiceId, decimal Amount, DateOnly PaidOn, string Method, string? Reference,
        DateTimeOffset CreatedAt, string? UserName);

    /// <summary>Guarda el registro aquí (gana: es el cambio más reciente de este equipo) y lo encola para los demás equipos.</summary>
    private static async Task PublishAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, string id, object data)
    {
        var json = JsonSerializer.SerializeToElement(data, VortexJson.Options);
        await ApplyAsync(c, tx, new ErpRecord(kind, id, now, json), force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.RecordUpserted, new ErpRecord(kind, id, now, json), subjectType: kind, subjectId: id);
    }

    /// <summary>Aplica un registro del ERP. El de otro equipo solo si es más nuevo que el que hay. Devuelve si lo aplicó.</summary>
    internal static async Task<bool> ApplyAsync(SqliteConnection c, SqliteTransaction tx, ErpRecord r, bool force = false)
    {
        var at = SqliteExtensions.Time(r.UpdatedAt);
        bool Newer(string? current) => force || current is null || string.CompareOrdinal(current, at) < 0;
        switch (r.Kind)
        {
            case ErpRecordKinds.PurchaseSettings:
                if (!Newer(await c.ScalarAsync<string?>("SELECT updated_at FROM erp_settings WHERE key = $k", tx, ("$k", r.Id))))
                    return false;
                await c.ExecAsync("""
                    INSERT INTO erp_settings (key, value, updated_at) VALUES ($k, $v, $at)
                    ON CONFLICT (key) DO UPDATE SET value = $v, updated_at = $at
                    """, tx, ("$k", r.Id), ("$v", r.Data.GetRawText()), ("$at", r.UpdatedAt));
                return true;

            case ErpRecordKinds.PurchaseRequest:
            {
                var d = r.Data.Deserialize<PurchaseRequestData>(VortexJson.Options)!;
                if (!Newer(await c.ScalarAsync<string?>("SELECT updated_at FROM purchase_requests WHERE id = $id", tx, ("$id", d.Id))))
                    return false;
                await c.ExecAsync("""
                    INSERT INTO purchase_requests (id, number, site_id, supplier_id, status, currency_code, total, notes, requested_by, requested_at,
                                                   decided_by, decided_at, reason, po_id, updated_at)
                    VALUES ($id, $number, $site, $supplier, $status, $currency, $total, $notes, $by, $at, $dby, $dat, $reason, $po, $upd)
                    ON CONFLICT (id) DO UPDATE SET number = $number, site_id = $site, supplier_id = $supplier, status = $status,
                        currency_code = $currency, total = $total, notes = $notes, requested_by = $by, requested_at = $at, decided_by = $dby,
                        decided_at = $dat, reason = $reason, po_id = $po, updated_at = $upd
                    """, tx, ("$id", d.Id), ("$number", d.Number), ("$site", d.SiteId), ("$supplier", d.SupplierId), ("$status", d.Status),
                    ("$currency", d.CurrencyCode), ("$total", d.Total), ("$notes", d.Notes), ("$by", d.RequestedBy), ("$at", d.RequestedAt),
                    ("$dby", d.DecidedBy), ("$dat", d.DecidedAt), ("$reason", d.Reason), ("$po", d.PurchaseOrderId), ("$upd", r.UpdatedAt));
                await c.ExecAsync("DELETE FROM purchase_request_lines WHERE request_id = $id", tx, ("$id", d.Id));
                var no = 0;
                foreach (var l in d.Lines)
                    await c.ExecAsync("INSERT INTO purchase_request_lines (request_id, line_no, item_id, quantity, unit_cost) VALUES ($id, $no, $item, $qty, $cost)",
                        tx, ("$id", d.Id), ("$no", ++no), ("$item", l.ItemId), ("$qty", l.Quantity), ("$cost", l.UnitCost));
                return true;
            }

            case ErpRecordKinds.SupplierInvoice:
            {
                var d = r.Data.Deserialize<SupplierInvoiceData>(VortexJson.Options)!;
                if (!Newer(await c.ScalarAsync<string?>("SELECT updated_at FROM supplier_invoices WHERE id = $id", tx, ("$id", d.Id))))
                    return false;
                await c.ExecAsync("""
                    INSERT INTO supplier_invoices (id, supplier_id, po_id, document_number, issued_on, due_on, currency_code, subtotal, tax, total,
                                                   status, match_note, released_by, released_at, release_reason, planned_on, created_at, user_name, updated_at)
                    VALUES ($id, $supplier, $po, $doc, $issued, $due, $currency, $sub, $tax, $total, $status, $note, $rby, $rat, $rreason, $planned,
                            $created, $user, $upd)
                    ON CONFLICT (id) DO UPDATE SET supplier_id = $supplier, po_id = $po, document_number = $doc, issued_on = $issued, due_on = $due,
                        currency_code = $currency, subtotal = $sub, tax = $tax, total = $total, status = $status, match_note = $note,
                        released_by = $rby, released_at = $rat, release_reason = $rreason, planned_on = $planned, created_at = $created,
                        user_name = $user, updated_at = $upd
                    """, tx, ("$id", d.Id), ("$supplier", d.SupplierId), ("$po", d.PurchaseOrderId), ("$doc", d.DocumentNumber),
                    ("$issued", d.IssuedOn.ToString("yyyy-MM-dd")), ("$due", d.DueOn.ToString("yyyy-MM-dd")), ("$currency", d.CurrencyCode),
                    ("$sub", d.Subtotal), ("$tax", d.Tax), ("$total", d.Total), ("$status", d.Status), ("$note", d.MatchNote),
                    ("$rby", d.ReleasedBy), ("$rat", d.ReleasedAt), ("$rreason", d.ReleaseReason), ("$planned", d.PlannedOn?.ToString("yyyy-MM-dd")),
                    ("$created", d.CreatedAt), ("$user", d.UserName), ("$upd", r.UpdatedAt));
                await c.ExecAsync("DELETE FROM supplier_invoice_lines WHERE invoice_id = $id", tx, ("$id", d.Id));
                var no = 0;
                foreach (var l in d.Lines)
                    await c.ExecAsync("INSERT INTO supplier_invoice_lines (invoice_id, line_no, item_id, quantity, unit_cost) VALUES ($id, $no, $item, $qty, $cost)",
                        tx, ("$id", d.Id), ("$no", ++no), ("$item", l.ItemId), ("$qty", l.Quantity), ("$cost", l.UnitCost));
                return true;
            }

            case ErpRecordKinds.SupplierPayment:
            {
                var d = r.Data.Deserialize<SupplierPaymentData>(VortexJson.Options)!;
                if (!Newer(await c.ScalarAsync<string?>("SELECT updated_at FROM supplier_payments WHERE id = $id", tx, ("$id", d.Id))))
                    return false;
                await c.ExecAsync("""
                    INSERT INTO supplier_payments (id, invoice_id, amount, paid_on, method, reference, created_at, user_name, updated_at)
                    VALUES ($id, $inv, $amount, $paid, $method, $ref, $created, $user, $upd)
                    ON CONFLICT (id) DO UPDATE SET invoice_id = $inv, amount = $amount, paid_on = $paid, method = $method, reference = $ref,
                        created_at = $created, user_name = $user, updated_at = $upd
                    """, tx, ("$id", d.Id), ("$inv", d.InvoiceId), ("$amount", d.Amount), ("$paid", d.PaidOn.ToString("yyyy-MM-dd")),
                    ("$method", d.Method), ("$ref", d.Reference), ("$created", d.CreatedAt), ("$user", d.UserName), ("$upd", r.UpdatedAt));
                return true;
            }

            default:
                return false;
        }
    }
}
