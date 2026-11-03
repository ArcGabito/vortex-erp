using VortexErp.Core.Data;
using VortexErp.Core.Domain;

namespace VortexErp.Core.Services;

/// <summary>Inventario valorizado (con el stock que informa el WMS) e indicadores del panel.</summary>
public sealed class ReportService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    /// <summary>Costo unitario: el de la última orden de compra del artículo o, si no hubo compras, su precio de lista.</summary>
    private const string UnitCost = """
        COALESCE((SELECT l.unit_cost FROM purchase_order_lines l JOIN purchase_orders o ON o.id = l.po_id
                  WHERE l.item_id = s.item_id AND o.status <> 'cancelled' ORDER BY o.created_at DESC LIMIT 1), i.price)
        """;

    public async Task<PagedResult<StockRow>> GetStockAsync(string? search, Guid? siteId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE ($site IS NULL OR s.site_id = $site)
              AND ($search IS NULL OR i.sku LIKE $like OR i.name LIKE $like OR i.category LIKE $like)
            """;
        var args = new (string, object?)[]
        {
            ("$site", siteId), ("$search", Clean(search)), ("$like", Like(search)), ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        const string from = "FROM stock s JOIN items i ON i.id = s.item_id LEFT JOIN sites t ON t.id = s.site_id";
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) {from} {where}", null, args);
        var rows = await c.QueryAsync($"""
            SELECT s.*, COALESCE(t.name, '?') AS site_name, i.sku, i.name AS item_name, i.category, i.unit,
                   {UnitCost} AS unit_cost,
                   EXISTS (SELECT 1 FROM purchase_order_lines l WHERE l.item_id = s.item_id) AS from_purchase
            {from} {where} ORDER BY i.name COLLATE NOCASE, t.name LIMIT $limit OFFSET $offset
            """, r => new StockRow(r.Guid("site_id"), r.Str("site_name"), r.Guid("item_id"), r.Str("sku"), r.Str("item_name"),
                r.StrOrNull("category"), r.Str("unit"), r.Dec("on_hand"), r.Dec("unit_cost"), r.Bool("from_purchase"), r.Time("updated_at")),
            null, args);
        return new PagedResult<StockRow>(rows, total, page, pageSize);
    }

    public async Task<decimal> GetInventoryValueAsync(Guid? siteId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await InventoryValueAsync(c, siteId);
    }

    public async Task<DashboardSummary> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var (thisMonth, nextMonth) = Month(0);
        var (lastMonth, _) = Month(-1);
        var now = Clock.GetUtcNow();
        var revenue = await RevenueAsync(c, thisMonth, nextMonth);
        // Comparación justa: el mes pasado hasta el mismo día.
        var previous = await RevenueAsync(c, lastMonth, lastMonth + (now - thisMonth));
        var today = DateOnly.FromDateTime(now.ToOffset(ErpClock.Offset).DateTime).ToString("yyyy-MM-dd");

        var receivable = await c.QueryAsync("""
            SELECT COALESCE(SUM(total - paid), 0) AS balance,
                   COALESCE(SUM(CASE WHEN due_date < $today THEN total - paid ELSE 0 END), 0) AS overdue,
                   COUNT(CASE WHEN due_date < $today THEN 1 END) AS overdue_count
            FROM invoices WHERE total - paid > 0
            """, r => (Balance: r.Dec("balance"), Overdue: r.Dec("overdue"), Count: r.Int("overdue_count")), null, ("$today", today));
        var currency = await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1") ?? "PEN";

        return new DashboardSummary(
            revenue, previous > 0 ? Math.Round((revenue - previous) / previous * 100, 1) : null,
            receivable[0].Balance, receivable[0].Overdue, receivable[0].Count,
            await c.ScalarAsync<int>("SELECT COUNT(*) FROM sales_orders WHERE status <> 'cancelled' AND invoice_id IS NULL"),
            await c.ScalarAsync<int>("SELECT COUNT(*) FROM accepted_quotes WHERE order_id IS NULL"),
            await c.ScalarAsync<int>("SELECT COUNT(*) FROM purchase_orders WHERE status IN ('open', 'partial')"),
            await InventoryValueAsync(c, null),
            await c.ScalarAsync<int>("SELECT COUNT(*) FROM pos_sales WHERE status = 'completed' AND invoice_id IS NULL"),
            currency);
    }

    /// <summary>Ingresos de los últimos <paramref name="months"/> meses: comprobantes de pedidos y ventas de caja netas.</summary>
    public async Task<IReadOnlyList<MonthRevenue>> GetMonthlyRevenueAsync(int months = 6, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var list = new List<MonthRevenue>();
        for (var i = months - 1; i >= 0; i--)
        {
            var (from, to) = Month(-i);
            var invoiced = await c.ScalarAsync<decimal>(
                "SELECT COALESCE(SUM(total), 0) FROM invoices WHERE sale_id IS NULL AND issued_at >= $from AND issued_at < $to", null,
                ("$from", from), ("$to", to));
            var pos = await PosNetAsync(c, from, to);
            list.Add(new MonthRevenue(DateOnly.FromDateTime(from.ToOffset(ErpClock.Offset).DateTime), invoiced, pos));
        }
        return list;
    }

    // Ingresos = comprobantes de pedidos (los de ventas de caja ya cuentan como venta) + ventas de caja netas de devoluciones.
    private static async Task<decimal> RevenueAsync(Microsoft.Data.Sqlite.SqliteConnection c, DateTimeOffset from, DateTimeOffset to) =>
        await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(total), 0) FROM invoices WHERE sale_id IS NULL AND issued_at >= $from AND issued_at < $to",
            null, ("$from", from), ("$to", to)) + await PosNetAsync(c, from, to);

    private static Task<decimal> PosNetAsync(Microsoft.Data.Sqlite.SqliteConnection c, DateTimeOffset from, DateTimeOffset to) =>
        c.ScalarAsync<decimal>("SELECT COALESCE(SUM(total - returned), 0) FROM pos_sales WHERE status = 'completed' AND created_at >= $from AND created_at < $to",
            null, ("$from", from), ("$to", to));

    private static Task<decimal> InventoryValueAsync(Microsoft.Data.Sqlite.SqliteConnection c, Guid? siteId) =>
        c.ScalarAsync<decimal>($"""
            SELECT COALESCE(SUM(s.on_hand * {UnitCost}), 0)
            FROM stock s JOIN items i ON i.id = s.item_id WHERE s.on_hand > 0 AND ($site IS NULL OR s.site_id = $site)
            """, null, ("$site", siteId));

    /// <summary>Inicio y fin del mes (hora de Perú) desplazado <paramref name="offset"/> meses, como instantes UTC.</summary>
    private (DateTimeOffset From, DateTimeOffset To) Month(int offset)
    {
        var local = Clock.GetUtcNow().ToOffset(ErpClock.Offset);
        var start = new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, ErpClock.Offset).AddMonths(offset);
        return (start.ToUniversalTime(), start.AddMonths(1).ToUniversalTime());
    }
}
