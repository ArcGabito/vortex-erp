using Microsoft.Data.Sqlite;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Sync;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Services;

/// <summary>
/// Pedidos de venta. Se crean en el ERP, llegan del CRM o del WMS (<c>order.created</c>) o salen de una cotización
/// aceptada en el CRM. El WMS los despacha (<c>shipment.dispatched</c>) y el ERP los factura.
/// </summary>
public sealed class OrderService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    /// <summary>
    /// Id del pedido que sale de una cotización. El CRM usa el mismo cálculo al generar el pedido, así que si los dos lo
    /// crean, el hub recibe un solo <c>order.created</c>.
    /// </summary>
    public static Guid OrderIdForQuote(Guid quoteId) => DeterministicId.From("order_from_quote", quoteId);

    public Task<string> CreateAsync(Guid customerId, Guid? siteId, IReadOnlyList<DocumentLine> lines, string currencyCode,
        CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.CreateOrder);
        return InTransactionAsync((c, tx, now) => CreateInternalAsync(c, tx, now, Guid.NewGuid(), customerId, siteId, lines, currencyCode, null), ct);
    }

    /// <summary>Convierte una cotización aceptada del CRM en pedido (o la vincula si el CRM ya lo creó).</summary>
    public Task<string> ConvertQuoteAsync(Guid quoteId, Guid? siteId, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ConvertQuote);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var quote = await c.FirstOrDefaultAsync("SELECT * FROM accepted_quotes WHERE id = $id", r => new
            {
                CustomerId = r.GuidOrNull("customer_id"), Currency = r.Str("currency_code"), OrderId = r.GuidOrNull("order_id"),
            }, tx, ("$id", quoteId));
            Require(quote is not null, T("error.la_cotizacion_no_existe"));
            Require(quote!.OrderId is null, T("error.la_cotizacion_ya_tiene_pedido"));
            Require(quote.CustomerId is not null, T("error.la_cotizacion_no_tiene_cliente"));

            var orderId = OrderIdForQuote(quoteId);
            if (await c.ScalarAsync<string?>("SELECT number FROM sales_orders WHERE id = $id", tx, ("$id", orderId)) is { } existing)
            {
                await c.ExecAsync("UPDATE accepted_quotes SET order_id = $order WHERE id = $id", tx, ("$id", quoteId), ("$order", orderId));
                return existing;
            }

            var lines = await c.QueryAsync("SELECT * FROM accepted_quote_lines WHERE quote_id = $id ORDER BY line_no",
                r => new DocumentLine(r.Guid("item_id"), r.Dec("quantity"), r.Dec("unit_price")), tx, ("$id", quoteId));
            return await CreateInternalAsync(c, tx, now, orderId, quote.CustomerId!.Value, siteId, lines, quote.Currency, quoteId);
        }, ct);
    }

    public Task CancelAsync(Guid orderId, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.CancelOrder);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var order = await GetAsync(c, tx, orderId);
            Require(order is not null, T("error.el_pedido_no_existe"));
            Require(order!.CanCancel, T("error.solo_se_cancela_un_pedido"));
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.Cancelled, "order", orderId, order.Number,
                $"Total {order.Total:0.##} {order.CurrencyCode}");
            return await c.ExecAsync("UPDATE sales_orders SET status = 'cancelled' WHERE id = $id", tx, ("$id", orderId));
        }, ct);
    }

    // ---------- Consultas ----------

    public async Task<PagedResult<OrderRow>> GetOrdersAsync(string? search, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var where = $"""
            WHERE (1 = 1{SiteFilterOrNull("o.site_id")})
              AND ($status IS NULL OR o.status = $status OR ($status = 'uninvoiced' AND o.status <> 'cancelled' AND o.invoice_id IS NULL
                   AND (NOT EXISTS (SELECT 1 FROM order_shipments s WHERE s.order_id = o.id)
                        OR EXISTS (SELECT 1 FROM order_shipments s WHERE s.order_id = o.id AND s.invoice_id IS NULL)))
                   OR ($status = 'pending_approval' AND o.approval = 'pending' AND o.status <> 'cancelled'))
              AND ($search IS NULL OR o.number LIKE $like OR p.name LIKE $like OR p.document_number LIKE $like)
            """;
        var args = new (string, object?)[]
        {
            ("$status", Clean(status)), ("$search", Clean(search)), ("$like", Like(search)), ("$limit", pageSize),
            ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM sales_orders o LEFT JOIN parties p ON p.id = o.customer_id {where}", null, args);
        var rows = await c.QueryAsync($"{Select} {where} ORDER BY o.created_at DESC LIMIT $limit OFFSET $offset", Read, null, args);
        return new PagedResult<OrderRow>(rows, total, page, pageSize);
    }

    public async Task<OrderRow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await GetAsync(c, null, id);
    }

    public async Task<IReadOnlyList<OrderLineRow>> GetLinesAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await LinesAsync(c, null, orderId);
    }

    public async Task<IReadOnlyList<QuoteRow>> GetQuotesAsync(bool pendingOnly, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT q.*, p.name AS customer_name, o.number AS order_number,
                   (SELECT COUNT(*) FROM accepted_quote_lines l WHERE l.quote_id = q.id) AS line_count
            FROM accepted_quotes q LEFT JOIN parties p ON p.id = q.customer_id LEFT JOIN sales_orders o ON o.id = q.order_id
            WHERE ($pending = 0 OR q.order_id IS NULL)
            ORDER BY q.accepted_at DESC
            """, r => new QuoteRow(r.Guid("id"), r.StrOrNull("number"), r.GuidOrNull("customer_id"), r.StrOrNull("customer_name"),
                r.Str("currency_code"), r.Dec("total"), r.Time("accepted_at"), r.GuidOrNull("order_id"), r.StrOrNull("order_number"),
                r.Int("line_count")), null, ("$pending", pendingOnly));
    }

    // ---------- Lo que llega de otros sistemas ----------

    /// <summary><c>order.created</c> del CRM, del WMS o de otro equipo del ERP. Idempotente por <c>orderId</c>.</summary>
    internal static async Task ApplyOrderCreatedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, string source, OrderCreated e)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM sales_orders WHERE id = $id", tx, ("$id", e.OrderId)) > 0)
            return;
        var number = string.IsNullOrWhiteSpace(e.Number) ? $"{source}-{e.OrderId.ToString()[..8].ToUpperInvariant()}" : e.Number;
        await InsertAsync(c, tx, at, e.OrderId, number, source, e.CustomerId == Guid.Empty ? null : e.CustomerId, e.SiteId,
            e.Lines ?? [], e.CurrencyCode ?? "PEN", e.QuoteId, null, e.ApprovalStatus == OrderApprovals.Pending ? OrderApprovals.Pending : null);
        if (e.QuoteId is { } quoteId)
            await c.ExecAsync("UPDATE accepted_quotes SET order_id = $order WHERE id = $id AND order_id IS NULL", tx,
                ("$id", quoteId), ("$order", e.OrderId));
    }

    internal static async Task ApplyQuoteAcceptedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, QuoteAccepted e)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM accepted_quotes WHERE id = $id", tx, ("$id", e.QuoteId)) > 0)
            return;
        // Si el CRM ya generó el pedido de esta cotización, queda vinculado.
        var orderId = OrderIdForQuote(e.QuoteId);
        var hasOrder = await c.ScalarAsync<long>("SELECT COUNT(*) FROM sales_orders WHERE id = $id", tx, ("$id", orderId)) > 0;
        await c.ExecAsync("""
            INSERT INTO accepted_quotes (id, number, customer_id, currency_code, total, accepted_at, order_id)
            VALUES ($id, $number, $customer, $currency, $total, $at, $order)
            """, tx, ("$id", e.QuoteId), ("$number", e.Number), ("$customer", e.CustomerId), ("$currency", e.CurrencyCode ?? "PEN"),
            ("$total", e.Total), ("$at", at), ("$order", hasOrder ? orderId : null));
        var lineNo = 0;
        foreach (var l in (e.Lines ?? []).Where(l => l.ItemId != Guid.Empty && l.Quantity > 0))
        {
            await c.ExecAsync("""
                INSERT INTO accepted_quote_lines (quote_id, line_no, item_id, quantity, unit_price) VALUES ($id, $no, $item, $qty, $price)
                """, tx, ("$id", e.QuoteId), ("$no", ++lineNo), ("$item", l.ItemId), ("$qty", l.Quantity), ("$price", l.UnitPrice));
        }
    }

    internal static Task ApplyDealWonAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, DealWon e) =>
        c.ExecAsync("""
            INSERT OR IGNORE INTO won_deals (id, customer_id, amount, currency_code, quote_id, won_at)
            VALUES ($id, $customer, $amount, $currency, $quote, $at)
            """, tx, ("$id", e.DealId), ("$customer", e.CustomerId), ("$amount", e.Amount), ("$currency", e.CurrencyCode ?? "PEN"),
            ("$quote", e.QuoteId), ("$at", at));

    /// <summary>
    /// <c>shipment.dispatched</c> del WMS: guarda el despacho con lo que salió (sus líneas; sin líneas, todo lo que faltaba)
    /// y el pedido queda despachado o despachado en parte. Cada despacho se puede facturar por separado.
    /// </summary>
    internal static async Task ApplyDispatchedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, ShipmentDispatched e)
    {
        var status = await c.ScalarAsync<string?>("SELECT status FROM sales_orders WHERE id = $id", tx, ("$id", e.OrderId));
        if (status is null || await c.ScalarAsync<long>("SELECT COUNT(*) FROM order_shipments WHERE id = $id", tx, ("$id", e.ShipmentId)) > 0)
            return;
        var lines = e.Lines is { Count: > 0 }
            ? e.Lines.GroupBy(l => l.ItemId).Select(g => (ItemId: g.Key, Quantity: g.Sum(l => l.Quantity))).ToList()
            : await RemainingAsync(c, tx, e.OrderId);
        await c.ExecAsync("""
            INSERT INTO order_shipments (id, order_id, number, dispatched_at, partial, carrier, tracking_number)
            VALUES ($id, $order, $number, $at, $partial, $carrier, $tracking)
            """, tx, ("$id", e.ShipmentId), ("$order", e.OrderId), ("$number", e.Number), ("$at", at), ("$partial", e.Partial == true),
            ("$carrier", e.Carrier), ("$tracking", e.TrackingNumber));
        foreach (var l in lines.Where(l => l.Quantity > 0))
            await c.ExecAsync("INSERT INTO order_shipment_lines (shipment_id, item_id, quantity) VALUES ($id, $item, $qty)", tx,
                ("$id", e.ShipmentId), ("$item", l.ItemId), ("$qty", l.Quantity));
        if (status == OrderStatuses.Cancelled)
            return;
        await c.ExecAsync("""
            UPDATE sales_orders SET status = $status, dispatched_at = $at, shipment_number = $number, carrier = $carrier,
                                    tracking_number = $tracking
            WHERE id = $id
            """, tx, ("$id", e.OrderId), ("$status", e.Partial == true ? OrderStatuses.Partial : OrderStatuses.Dispatched), ("$at", at),
            ("$number", e.Number), ("$carrier", e.Carrier), ("$tracking", e.TrackingNumber));
    }

    /// <summary>Lo que falta despachar del pedido (pedido menos lo ya despachado), por artículo.</summary>
    private static async Task<List<(Guid ItemId, decimal Quantity)>> RemainingAsync(SqliteConnection c, SqliteTransaction tx, Guid orderId) =>
        await c.QueryAsync("""
            SELECT l.item_id, SUM(l.quantity) - COALESCE((SELECT SUM(sl.quantity) FROM order_shipment_lines sl
                JOIN order_shipments s ON s.id = sl.shipment_id WHERE s.order_id = $id AND sl.item_id = l.item_id), 0) AS remaining
            FROM sales_order_lines l WHERE l.order_id = $id GROUP BY l.item_id
            """, r => (r.Guid("item_id"), r.Dec("remaining")), tx, ("$id", orderId));

    /// <summary>Despachos del pedido, con lo que salió valorizado al precio del pedido.</summary>
    public async Task<IReadOnlyList<ShipmentRow>> GetShipmentsAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ShipmentsAsync(c, null, orderId);
    }

    internal static async Task<List<ShipmentRow>> ShipmentsAsync(SqliteConnection c, SqliteTransaction? tx, Guid orderId, Guid? shipmentId = null)
    {
        var prices = (await LinesAsync(c, tx, orderId)).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.First());
        var shipments = await c.QueryAsync("""
            SELECT s.*, i.number AS invoice_number FROM order_shipments s LEFT JOIN invoices i ON i.id = s.invoice_id
            WHERE s.order_id = $id AND ($shipment IS NULL OR s.id = $shipment) ORDER BY s.dispatched_at
            """, r => new
            {
                Id = r.Guid("id"), Number = r.StrOrNull("number"), At = r.Time("dispatched_at"), Partial = r.Bool("partial"),
                Carrier = r.StrOrNull("carrier"), Tracking = r.StrOrNull("tracking_number"), Invoice = r.GuidOrNull("invoice_id"),
                InvoiceNumber = r.StrOrNull("invoice_number"),
            }, tx, ("$id", orderId), ("$shipment", shipmentId));
        var result = new List<ShipmentRow>();
        foreach (var s in shipments)
        {
            var lines = await c.QueryAsync("SELECT item_id, quantity FROM order_shipment_lines WHERE shipment_id = $id",
                r => (Item: r.Guid("item_id"), Qty: r.Dec("quantity")), tx, ("$id", s.Id));
            result.Add(new ShipmentRow(s.Id, orderId, s.Number, s.At, s.Partial, s.Carrier, s.Tracking, s.Invoice, s.InvoiceNumber,
                lines.Select((l, i) => prices.TryGetValue(l.Item, out var p)
                    ? new OrderLineRow(i + 1, l.Item, p.Sku, p.ItemName, l.Qty, p.UnitPrice)
                    : new OrderLineRow(i + 1, l.Item, "?", "?", l.Qty, 0)).ToList()));
        }
        return result;
    }

    // ---------- Internos ----------

    private async Task<string> CreateInternalAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid orderId, Guid customerId,
        Guid? siteId, IReadOnlyList<DocumentLine> lines, string currencyCode, Guid? quoteId)
    {
        Require(customerId != Guid.Empty, T("error.elige_el_cliente"));
        Require(lines.Count > 0, T("error.agrega_al_menos_un_articulo"));
        Require(lines.All(l => l.Quantity > 0), T("error.las_cantidades_deben_ser_mayores"));
        Require(lines.All(l => (l.UnitPrice ?? 0) >= 0), T("error.los_precios_no_pueden_ser"));
        Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM parties WHERE id = $id AND is_deleted = 0", tx, ("$id", customerId)) > 0,
            T("error.el_cliente_no_existe"));
        var site = siteId ?? await DefaultSiteAsync(c, tx);
        Require(site is null || CanUseSite(site.Value), T("error.sede_no_asignada"));

        var terms = (await c.QueryAsync("SELECT payment_term_days, credit_limit, discount_percent, price_list_id FROM parties WHERE id = $id",
            MasterDataService.ReadTerms, tx, ("$id", customerId))).Single();
        var book = await PriceListService.LoadAsync(c, tx, now, customerId, site, currencyCode);
        var orderLines = new List<OrderLine>();
        foreach (var l in lines)
        {
            // Sin precio escrito: el de la lista del cliente o de la sede (con la escala de lo que se pide del artículo) o
            // el del artículo con el descuento habitual del cliente.
            var price = l.UnitPrice;
            if (price is null)
                price = book.ListPrice(l.ItemId, lines.Where(x => x.ItemId == l.ItemId).Sum(x => x.Quantity))
                        ?? book.PriceFor(l.ItemId, l.Quantity, await ListPriceAsync(c, tx, l.ItemId, currencyCode, now));
            Require(price is not null, T("error.uno_de_los_articulos_ya"));
            orderLines.Add(new OrderLine(l.ItemId, l.Quantity, price!.Value));
        }

        // Límite de crédito: si lo que debe el cliente, más sus pedidos abiertos, más este pedido lo supera, el pedido
        // queda por aprobar (no se prepara ni se factura hasta entonces).
        string? approval = null;
        var orderTotal = orderLines.Sum(l => Tax.Round(l.Quantity * l.UnitPrice));
        if (terms.CreditLimit is { } limit)
        {
            var today = DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime);
            var (exposure, _) = await Credit.ExposureAsync(c, tx, customerId, today);
            var after = exposure + await Credit.ToCompanyAsync(c, tx, orderTotal, currencyCode, today);
            if (after > limit)
                approval = OrderApprovals.Pending;
        }

        var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Order, Context.DeviceTag);
        var total = await InsertAsync(c, tx, now, orderId, number, ErpSyncHandlers.ProductCode, customerId, site, orderLines, currencyCode,
            quoteId, Context.UserName, approval);
        if (quoteId is { } q)
            await c.ExecAsync("UPDATE accepted_quotes SET order_id = $order WHERE id = $id", tx, ("$id", q), ("$order", orderId));
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.OrderCreated,
            new OrderCreated(orderId, number, customerId, site, orderLines, total, currencyCode, quoteId, approval),
            subjectType: "order", subjectId: number, id: orderId);
        if (approval is not null)
            await AuditAsync(c, tx, now, "order.credit_hold", "order", orderId, number,
                $"Supera el límite de crédito ({terms.CreditLimit:0.##}); queda por aprobar");
        return number;
    }

    /// <summary>
    /// Aprueba un pedido que superaba el límite de crédito: desde ahí se prepara y se factura. Viaja como
    /// <c>order.approved</c> para los demás equipos del ERP y el WMS.
    /// </summary>
    public Task ApproveAsync(Guid orderId, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ApproveCredit);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var order = await GetAsync(c, tx, orderId);
            Require(order is not null, T("error.el_pedido_no_existe"));
            Require(order!.PendingApproval && order.Status != OrderStatuses.Cancelled, T("error.el_pedido_no_esta_por_aprobar"));
            await ApplyApprovedAsync(c, tx, now, orderId, Context.UserName);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.OrderApproved, new OrderApproved(orderId, order.Number, Context.UserName),
                subjectType: "order", subjectId: order.Number, id: DeterministicId.From("order_approved", orderId));
            await AuditAsync(c, tx, now, "order.approved", "order", orderId, order.Number,
                $"Aprobado sobre el límite de crédito · {order.Total:0.##} {order.CurrencyCode}");
            return 0;
        }, ct);
    }

    internal static Task ApplyApprovedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, Guid orderId, string? by) =>
        c.ExecAsync("""
            UPDATE sales_orders SET approval = 'approved', approved_by = $by, approved_at = $at WHERE id = $id AND approval = 'pending'
            """, tx, ("$id", orderId), ("$by", by), ("$at", at));

    /// <summary>
    /// Precio de lista del artículo en la moneda del pedido: si el artículo tiene otra moneda, se convierte con el tipo de
    /// cambio del día (sin tipo de cambio no se puede vender en esa moneda).
    /// </summary>
    private static async Task<decimal?> ListPriceAsync(SqliteConnection c, SqliteTransaction tx, Guid itemId, string currency, DateTimeOffset now)
    {
        var item = await c.FirstOrDefaultAsync("SELECT price, currency_code FROM items WHERE id = $id",
            r => new { Price = r.Dec("price"), Currency = r.Str("currency_code") }, tx, ("$id", itemId));
        if (item is null)
            return null;
        var rate = await Fx.RateAsync(c, tx, item.Currency, currency, DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime));
        Require(rate is not null, T("error.sin_tipo_de_cambio", item.Currency, currency));
        return VortexApi.Suite.Money.Currencies.Get(currency).Round(item.Price * rate!.Value);
    }

    private static async Task<decimal> InsertAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, Guid id, string number,
        string source, Guid? customerId, Guid? siteId, IReadOnlyList<OrderLine> lines, string currencyCode, Guid? quoteId, string? userName,
        string? approval = null)
    {
        var valid = lines.Where(l => l.ItemId != Guid.Empty && l.Quantity > 0).ToList();
        var total = valid.Sum(l => Tax.Round(l.Quantity * l.UnitPrice));
        await c.ExecAsync("""
            INSERT INTO sales_orders (id, number, source, customer_id, site_id, quote_id, status, currency_code, total, created_at, user_name,
                                      approval)
            VALUES ($id, $number, $source, $customer, $site, $quote, 'open', $currency, $total, $at, $user, $approval)
            """, tx, ("$id", id), ("$number", number), ("$source", source), ("$customer", customerId), ("$site", siteId),
            ("$quote", quoteId), ("$currency", currencyCode), ("$total", total), ("$at", at), ("$user", userName), ("$approval", approval));
        var lineNo = 0;
        foreach (var l in valid)
        {
            await c.ExecAsync("""
                INSERT INTO sales_order_lines (order_id, line_no, item_id, quantity, unit_price) VALUES ($id, $no, $item, $qty, $price)
                """, tx, ("$id", id), ("$no", ++lineNo), ("$item", l.ItemId), ("$qty", l.Quantity), ("$price", l.UnitPrice));
        }
        return total;
    }

    private const string Select = """
        SELECT o.*, p.name AS customer_name, t.name AS site_name, i.number AS invoice_number,
               (SELECT COUNT(*) FROM sales_order_lines l WHERE l.order_id = o.id) AS line_count,
               (SELECT COUNT(*) FROM order_shipments s WHERE s.order_id = o.id AND s.invoice_id IS NULL) AS pending_shipments,
               (SELECT COUNT(*) FROM order_shipments s WHERE s.order_id = o.id AND s.invoice_id IS NOT NULL) AS invoiced_shipments
        FROM sales_orders o LEFT JOIN parties p ON p.id = o.customer_id LEFT JOIN sites t ON t.id = o.site_id
             LEFT JOIN invoices i ON i.id = o.invoice_id
        """;

    internal static Task<OrderRow?> GetAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        c.FirstOrDefaultAsync($"{Select} WHERE o.id = $id", Read, tx, ("$id", id));

    private static OrderRow Read(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("number"), r.Str("source"), r.GuidOrNull("customer_id"), r.StrOrNull("customer_name"), r.GuidOrNull("site_id"),
        r.StrOrNull("site_name"), r.GuidOrNull("quote_id"), r.Str("status"), r.Str("currency_code"), r.Dec("total"), r.Time("created_at"),
        r.StrOrNull("user_name"), r.TimeOrNull("dispatched_at"), r.StrOrNull("shipment_number"), r.StrOrNull("carrier"),
        r.StrOrNull("tracking_number"), r.GuidOrNull("invoice_id"), r.StrOrNull("invoice_number"), r.Int("line_count"),
        r.StrOrNull("approval"), r.StrOrNull("approved_by"), r.Int("pending_shipments"), r.Int("invoiced_shipments"));

    internal static async Task<List<OrderLineRow>> LinesAsync(SqliteConnection c, SqliteTransaction? tx, Guid orderId) =>
        await c.QueryAsync("""
            SELECT l.*, COALESCE(i.sku, '?') AS sku, COALESCE(i.name, 'Artículo desconocido') AS item_name
            FROM sales_order_lines l LEFT JOIN items i ON i.id = l.item_id WHERE l.order_id = $id ORDER BY l.line_no
            """, r => new OrderLineRow(r.Int("line_no"), r.Guid("item_id"), r.Str("sku"), r.Str("item_name"), r.Dec("quantity"),
                r.Dec("unit_price")), tx, ("$id", orderId));
}
