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
        const string where = """
            WHERE ($status IS NULL OR o.status = $status OR ($status = 'uninvoiced' AND o.status <> 'cancelled' AND o.invoice_id IS NULL))
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
            e.Lines ?? [], e.CurrencyCode ?? "PEN", e.QuoteId, null);
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

    internal static Task ApplyDispatchedAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, ShipmentDispatched e) =>
        c.ExecAsync("""
            UPDATE sales_orders SET status = 'dispatched', dispatched_at = $at, shipment_number = $number, carrier = $carrier,
                                    tracking_number = $tracking
            WHERE id = $id AND status = 'open'
            """, tx, ("$id", e.OrderId), ("$at", at), ("$number", e.Number), ("$carrier", e.Carrier), ("$tracking", e.TrackingNumber));

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

        var orderLines = new List<OrderLine>();
        foreach (var l in lines)
        {
            var price = l.UnitPrice ?? await c.ScalarAsync<decimal?>("SELECT price FROM items WHERE id = $id", tx, ("$id", l.ItemId));
            Require(price is not null, T("error.uno_de_los_articulos_ya"));
            orderLines.Add(new OrderLine(l.ItemId, l.Quantity, price!.Value));
        }

        var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Order, Context.DeviceTag);
        var total = await InsertAsync(c, tx, now, orderId, number, ErpSyncHandlers.ProductCode, customerId, site, orderLines, currencyCode,
            quoteId, Context.UserName);
        if (quoteId is { } q)
            await c.ExecAsync("UPDATE accepted_quotes SET order_id = $order WHERE id = $id", tx, ("$id", q), ("$order", orderId));
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.OrderCreated,
            new OrderCreated(orderId, number, customerId, site, orderLines, total, currencyCode, quoteId),
            subjectType: "order", subjectId: number, id: orderId);
        return number;
    }

    private static async Task<decimal> InsertAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, Guid id, string number,
        string source, Guid? customerId, Guid? siteId, IReadOnlyList<OrderLine> lines, string currencyCode, Guid? quoteId, string? userName)
    {
        var valid = lines.Where(l => l.ItemId != Guid.Empty && l.Quantity > 0).ToList();
        var total = valid.Sum(l => Tax.Round(l.Quantity * l.UnitPrice));
        await c.ExecAsync("""
            INSERT INTO sales_orders (id, number, source, customer_id, site_id, quote_id, status, currency_code, total, created_at, user_name)
            VALUES ($id, $number, $source, $customer, $site, $quote, 'open', $currency, $total, $at, $user)
            """, tx, ("$id", id), ("$number", number), ("$source", source), ("$customer", customerId), ("$site", siteId),
            ("$quote", quoteId), ("$currency", currencyCode), ("$total", total), ("$at", at), ("$user", userName));
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
               (SELECT COUNT(*) FROM sales_order_lines l WHERE l.order_id = o.id) AS line_count
        FROM sales_orders o LEFT JOIN parties p ON p.id = o.customer_id LEFT JOIN sites t ON t.id = o.site_id
             LEFT JOIN invoices i ON i.id = o.invoice_id
        """;

    internal static Task<OrderRow?> GetAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        c.FirstOrDefaultAsync($"{Select} WHERE o.id = $id", Read, tx, ("$id", id));

    private static OrderRow Read(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("number"), r.Str("source"), r.GuidOrNull("customer_id"), r.StrOrNull("customer_name"), r.GuidOrNull("site_id"),
        r.StrOrNull("site_name"), r.GuidOrNull("quote_id"), r.Str("status"), r.Str("currency_code"), r.Dec("total"), r.Time("created_at"),
        r.StrOrNull("user_name"), r.TimeOrNull("dispatched_at"), r.StrOrNull("shipment_number"), r.StrOrNull("carrier"),
        r.StrOrNull("tracking_number"), r.GuidOrNull("invoice_id"), r.StrOrNull("invoice_number"), r.Int("line_count"));

    internal static async Task<List<OrderLineRow>> LinesAsync(SqliteConnection c, SqliteTransaction? tx, Guid orderId) =>
        await c.QueryAsync("""
            SELECT l.*, COALESCE(i.sku, '?') AS sku, COALESCE(i.name, 'Artículo desconocido') AS item_name
            FROM sales_order_lines l LEFT JOIN items i ON i.id = l.item_id WHERE l.order_id = $id ORDER BY l.line_no
            """, r => new OrderLineRow(r.Int("line_no"), r.Guid("item_id"), r.Str("sku"), r.Str("item_name"), r.Dec("quantity"),
                r.Dec("unit_price")), tx, ("$id", orderId));
}
