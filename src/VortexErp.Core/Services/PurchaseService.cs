using Microsoft.Data.Sqlite;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Sync;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Services;

/// <summary>
/// Órdenes de compra a proveedores. Al crearse publican <c>purchase_order.created</c> (el WMS prepara la recepción) y
/// se dan por recibidas con el <c>goods.received</c> del WMS.
/// </summary>
public sealed class PurchaseService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    /// <summary>
    /// Orden de compra directa. Si supera el umbral de aprobación de compras, solo la crea quien puede aprobar; los demás
    /// la piden con una solicitud.
    /// </summary>
    public Task<string> CreateAsync(Guid supplierId, Guid? siteId, IReadOnlyList<DocumentLine> lines, string currencyCode,
        DateOnly? expectedAt = null, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.CreatePurchase);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var total = lines.Sum(l => Tax.Round(l.Quantity * (l.UnitPrice ?? 0)));
            if (!Context.Can(ErpPermissions.ApprovePurchase))
                Require(!await PayablesService.NeedsApprovalAsync(c, tx, total, currencyCode, DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime)),
                    T("error.compra_supera_el_umbral"));
            return (await CreateInternalAsync(c, tx, now, Context, supplierId, siteId, lines, currencyCode, expectedAt, null)).Number;
        }, ct);
    }

    /// <summary>Crea la orden y publica <c>purchase_order.created</c> (también desde una solicitud aprobada).</summary>
    internal static async Task<(Guid Id, string Number)> CreateInternalAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now,
        IErpContext context, Guid supplierId, Guid? siteId, IReadOnlyList<DocumentLine> lines, string currencyCode, DateOnly? expectedAt,
        Guid? requestId)
    {
        Require(supplierId != Guid.Empty, T("error.elige_el_proveedor"));
        Require(lines.Count > 0, T("error.agrega_al_menos_un_articulo"));
        Require(lines.All(l => l.Quantity > 0), T("error.las_cantidades_deben_ser_mayores"));
        Require(lines.All(l => l.UnitPrice is >= 0), T("error.indica_el_costo_unitario_de"));
        Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM parties WHERE id = $id AND is_supplier = 1 AND is_deleted = 0", tx,
            ("$id", supplierId)) > 0, T("error.el_proveedor_no_existe"));
        var fallback = await c.ScalarAsync<string?>($"""
            SELECT id FROM sites WHERE is_deleted = 0 AND is_active = 1
            {(context.DefaultSiteId is { } d ? $"ORDER BY CASE WHEN id = '{d}' THEN 0 ELSE 1 END," : "ORDER BY")} code LIMIT 1
            """, tx);
        var site = siteId ?? (fallback is null ? null : Guid.Parse(fallback));
        Require(site is not null, T("error.elige_el_almacen_que_recibe"));
        Require(context.SiteIds.Count == 0 || context.SiteIds.Contains(site!.Value), T("error.sede_no_asignada"));

        var id = Guid.NewGuid();
        var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Purchase, context.DeviceTag);
        var poLines = lines.GroupBy(l => l.ItemId)
            .Select(g => new PurchaseOrderLine(g.Key, g.Sum(l => l.Quantity), g.First().UnitPrice!.Value)).ToList();
        var e = new PurchaseOrderCreated(id, number, supplierId, site!.Value, poLines, currencyCode,
            poLines.Sum(l => Tax.Round(l.Quantity * l.UnitCost)), expectedAt);
        await InsertAsync(c, tx, now, e, context.UserName);
        if (requestId is { } r)
            await c.ExecAsync("UPDATE purchase_orders SET request_id = $r WHERE id = $id", tx, ("$id", id), ("$r", r));
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.PurchaseOrderCreated, e, subjectType: "purchase_order", subjectId: number, id: id);
        return (id, number);
    }

    /// <summary>
    /// Cancela una orden abierta o recibida en parte: lo recibido queda y el resto ya no se espera. Se publica
    /// <c>purchase_order.cancelled</c> para el WMS y los demás equipos del ERP.
    /// </summary>
    public Task CancelAsync(Guid id, string? reason = null, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.CancelPurchase);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var status = await c.ScalarAsync<string?>("SELECT status FROM purchase_orders WHERE id = $id", tx, ("$id", id));
            Require(status is PurchaseStatuses.Open or PurchaseStatuses.Partial, T("error.solo_se_cancela_una_orden"));
            var number = await c.ScalarAsync<string>("SELECT number FROM purchase_orders WHERE id = $id", tx, ("$id", id));
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.Cancelled, "purchase_order", id, number, Clean(reason));
            await ApplyCancelledAsync(c, tx, new PurchaseOrderCancelled(id, number, Clean(reason)));
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.PurchaseOrderCancelled, new PurchaseOrderCancelled(id, number, Clean(reason)),
                subjectType: "purchase_order", subjectId: number, id: DeterministicId.From("purchase_order_cancel", id));
            return 0;
        }, ct);
    }

    /// <summary><c>purchase_order.cancelled</c> (de este u otro equipo del ERP).</summary>
    internal static Task ApplyCancelledAsync(SqliteConnection c, SqliteTransaction tx, PurchaseOrderCancelled e) =>
        c.ExecAsync("UPDATE purchase_orders SET status = 'cancelled', cancel_reason = $reason WHERE id = $id AND status <> 'cancelled'", tx,
            ("$id", e.PurchaseOrderId), ("$reason", e.Reason));

    public async Task<PagedResult<PurchaseRow>> GetPurchasesAsync(string? search, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var where = $"""
            WHERE (1 = 1{SiteFilter("o.site_id")})
              AND ($status IS NULL OR o.status = $status OR ($status = 'pending' AND o.status IN ('open', 'partial')))
              AND ($search IS NULL OR o.number LIKE $like OR p.name LIKE $like)
            """;
        var args = new (string, object?)[]
        {
            ("$status", Clean(status)), ("$search", Clean(search)), ("$like", Like(search)), ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM purchase_orders o LEFT JOIN parties p ON p.id = o.supplier_id {where}", null, args);
        var rows = await c.QueryAsync($"""
            SELECT o.*, p.name AS supplier_name, t.name AS site_name,
                   (SELECT COUNT(*) FROM purchase_order_lines l WHERE l.po_id = o.id) AS line_count,
                   (SELECT COALESCE(SUM(quantity), 0) FROM purchase_order_lines l WHERE l.po_id = o.id) AS ordered,
                   (SELECT COALESCE(SUM(received_qty), 0) FROM purchase_order_lines l WHERE l.po_id = o.id) AS received
            FROM purchase_orders o LEFT JOIN parties p ON p.id = o.supplier_id LEFT JOIN sites t ON t.id = o.site_id
            {where} ORDER BY o.created_at DESC LIMIT $limit OFFSET $offset
            """, r => new PurchaseRow(r.Guid("id"), r.Str("number"), r.Guid("supplier_id"), r.StrOrNull("supplier_name"), r.Guid("site_id"),
                r.StrOrNull("site_name"), r.Str("status"), r.Str("currency_code"), r.Dec("total"), r.Time("created_at"),
                r.StrOrNull("expected_at") is { } d ? DateOnly.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : null,
                r.TimeOrNull("received_at"), r.Int("line_count"), r.Dec("ordered"), r.Dec("received")), null, args);
        return new PagedResult<PurchaseRow>(rows, total, page, pageSize);
    }

    public async Task<IReadOnlyList<PurchaseLineRow>> GetLinesAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT l.*, COALESCE(i.sku, '?') AS sku, COALESCE(i.name, 'Artículo desconocido') AS item_name
            FROM purchase_order_lines l LEFT JOIN items i ON i.id = l.item_id WHERE l.po_id = $id ORDER BY l.line_no
            """, r => new PurchaseLineRow(r.Int("line_no"), r.Guid("item_id"), r.Str("sku"), r.Str("item_name"), r.Dec("quantity"),
                r.Dec("unit_cost"), r.Dec("received_qty")), null, ("$id", id));
    }

    // ---------- Lo que llega de otros sistemas y de otros equipos del ERP ----------

    internal static async Task ApplyCreatedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, PurchaseOrderCreated e)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM purchase_orders WHERE id = $id", tx, ("$id", e.PurchaseOrderId)) > 0)
            return;
        await InsertAsync(c, tx, at, e, null);
    }

    /// <summary>Recepción del WMS: suma lo recibido a las líneas de la orden y la da por recibida si se completó.</summary>
    internal static async Task<string?> ApplyGoodsReceivedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, GoodsReceived e)
    {
        if (e.PurchaseOrderId is not { } poId)
            return "recepción sin orden de compra";
        var status = await c.ScalarAsync<string?>("SELECT status FROM purchase_orders WHERE id = $id", tx, ("$id", poId));
        if (status is null)
            return "orden de compra desconocida";

        foreach (var line in e.Lines ?? [])
        {
            var left = line.Quantity;
            var poLines = await c.QueryAsync("SELECT line_no, quantity - received_qty AS pending FROM purchase_order_lines WHERE po_id = $id AND item_id = $item ORDER BY line_no",
                r => (No: r.Int("line_no"), Pending: r.Dec("pending")), tx, ("$id", poId), ("$item", line.ItemId));
            for (var i = 0; i < poLines.Count && left > 0; i++)
            {
                // La última línea del artículo absorbe lo recibido de más.
                var take = i == poLines.Count - 1 ? left : Math.Min(left, Math.Max(0, poLines[i].Pending));
                await c.ExecAsync("UPDATE purchase_order_lines SET received_qty = received_qty + $qty WHERE po_id = $id AND line_no = $no", tx,
                    ("$id", poId), ("$no", poLines[i].No), ("$qty", take));
                left -= take;
            }
        }

        var pending = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(MAX(quantity - received_qty, 0)), 0) FROM purchase_order_lines WHERE po_id = $id",
            tx, ("$id", poId));
        await c.ExecAsync("""
            UPDATE purchase_orders SET status = $status, received_at = CASE WHEN $status = 'received' THEN $at ELSE received_at END
            WHERE id = $id AND status <> 'cancelled'
            """, tx, ("$id", poId), ("$status", pending <= 0 ? PurchaseStatuses.Received : PurchaseStatuses.Partial), ("$at", at));
        return null;
    }

    private static async Task InsertAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, PurchaseOrderCreated e, string? userName)
    {
        var lines = (e.Lines ?? []).Where(l => l.ItemId != Guid.Empty && l.Quantity > 0).ToList();
        await c.ExecAsync("""
            INSERT INTO purchase_orders (id, number, supplier_id, site_id, status, currency_code, total, created_at, expected_at, user_name)
            VALUES ($id, $number, $supplier, $site, 'open', $currency, $total, $at, $expected, $user)
            """, tx, ("$id", e.PurchaseOrderId), ("$number", e.Number ?? e.PurchaseOrderId.ToString()[..8].ToUpperInvariant()),
            ("$supplier", e.SupplierId), ("$site", e.SiteId), ("$currency", e.CurrencyCode ?? "PEN"),
            ("$total", e.Total != 0 ? e.Total : lines.Sum(l => Tax.Round(l.Quantity * l.UnitCost))), ("$at", at),
            ("$expected", e.ExpectedAt?.ToString("yyyy-MM-dd")), ("$user", userName));
        var lineNo = 0;
        foreach (var l in lines)
        {
            await c.ExecAsync("""
                INSERT INTO purchase_order_lines (po_id, line_no, item_id, quantity, unit_cost) VALUES ($id, $no, $item, $qty, $cost)
                """, tx, ("$id", e.PurchaseOrderId), ("$no", ++lineNo), ("$item", l.ItemId), ("$qty", l.Quantity), ("$cost", l.UnitCost));
        }
    }
}
