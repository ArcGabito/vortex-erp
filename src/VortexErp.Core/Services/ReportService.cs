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
            SELECT COALESCE(SUM(total + debited - credited - paid), 0) AS balance,
                   COALESCE(SUM(CASE WHEN due_date < $today THEN total + debited - credited - paid ELSE 0 END), 0) AS overdue,
                   COUNT(CASE WHEN due_date < $today THEN 1 END) AS overdue_count
            FROM invoices WHERE status = 'issued' AND total + debited - credited - paid > 0
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
            var invoiced = await InvoicedAsync(c, from, to);
            var pos = await PosNetAsync(c, from, to);
            list.Add(new MonthRevenue(DateOnly.FromDateTime(from.ToOffset(ErpClock.Offset).DateTime), invoiced, pos));
        }
        return list;
    }

    // ---------- Panel: series, categorías y transacciones ----------

    /// <summary>Inicio (en hora de la empresa) del periodo que contiene <paramref name="day"/>. Las semanas empiezan el lunes.</summary>
    public static DateOnly PeriodStart(DateOnly day, TrendGrain grain) => grain switch
    {
        TrendGrain.Month => new DateOnly(day.Year, day.Month, 1),
        TrendGrain.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        _ => day,
    };

    public static DateOnly NextPeriod(DateOnly start, TrendGrain grain) => grain switch
    {
        TrendGrain.Month => start.AddMonths(1),
        TrendGrain.Week => start.AddDays(7),
        _ => start.AddDays(1),
    };

    /// <summary>Los últimos <paramref name="periods"/> periodos, terminando en el actual (incluido aunque esté en curso).</summary>
    public async Task<IReadOnlyList<TrendPoint>> GetTrendAsync(TrendGrain grain, int periods, CancellationToken ct = default)
    {
        var today = Today();
        var starts = new List<DateOnly> { PeriodStart(today, grain) };
        while (starts.Count < periods)
        {
            var previous = starts[0];
            starts.Insert(0, grain switch
            {
                TrendGrain.Month => previous.AddMonths(-1),
                TrendGrain.Week => previous.AddDays(-7),
                _ => previous.AddDays(-1),
            });
        }
        var from = ToUtc(starts[0]);
        var to = ToUtc(NextPeriod(starts[^1], grain));

        await using var c = await Db.OpenAsync(ct);
        var invoices = await c.QueryAsync($"""
            SELECT issued_at, total FROM invoices WHERE status = 'issued' AND sale_id IS NULL AND issued_at >= $from AND issued_at < $to
            UNION ALL
            {NotesSql}
            """,
            r => (At: r.Time("issued_at"), Amount: r.Dec("total")), null, ("$from", from), ("$to", to));
        var sales = await c.QueryAsync(
            "SELECT created_at, total - returned AS net FROM pos_sales WHERE status = 'completed' AND created_at >= $from AND created_at < $to",
            r => (At: r.Time("created_at"), Amount: r.Dec("net")), null, ("$from", from), ("$to", to));
        var orders = await c.QueryAsync(
            "SELECT created_at FROM sales_orders WHERE status <> 'cancelled' AND created_at >= $from AND created_at < $to",
            r => r.Time("created_at"), null, ("$from", from), ("$to", to));
        // Primera compra de cada cliente, en todo el historial.
        var firsts = await c.QueryAsync("""
            SELECT MIN(at) AS first_at FROM (
                SELECT customer_id, issued_at AS at FROM invoices WHERE customer_id IS NOT NULL AND status = 'issued'
                UNION ALL
                SELECT customer_id, created_at FROM pos_sales WHERE customer_id IS NOT NULL AND status = 'completed')
            GROUP BY customer_id HAVING MIN(at) >= $from AND MIN(at) < $to
            """, r => r.Time("first_at"), null, ("$from", from), ("$to", to));

        var index = starts.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        int Slot(DateTimeOffset at) => index.GetValueOrDefault(PeriodStart(LocalDay(at), grain), -1);
        var invoiced = new decimal[periods];
        var pos = new decimal[periods];
        var orderCount = new int[periods];
        var newCustomers = new int[periods];
        var documents = new int[periods];
        foreach (var (at, amount) in invoices)
            if (Slot(at) is var i and >= 0) { invoiced[i] += amount; documents[i]++; }
        foreach (var (at, amount) in sales)
            if (Slot(at) is var i and >= 0) { pos[i] += amount; documents[i]++; }
        foreach (var at in orders)
            if (Slot(at) is var i and >= 0) orderCount[i]++;
        foreach (var at in firsts)
            if (Slot(at) is var i and >= 0) newCustomers[i]++;

        return starts.Select((s, i) => new TrendPoint(s, invoiced[i], pos[i], orderCount[i], newCustomers[i], documents[i])).ToList();
    }

    /// <summary>
    /// Ingresos por categoría de artículo entre <paramref name="from"/> y <paramref name="to"/> (fechas de la empresa,
    /// <paramref name="to"/> incluido) y en el periodo anterior del mismo largo. Por líneas: no descuenta devoluciones de caja.
    /// </summary>
    public async Task<IReadOnlyList<CategoryRevenue>> GetRevenueByCategoryAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        var prevFrom = from.AddDays(-days);
        await using var c = await Db.OpenAsync(ct);
        async Task<Dictionary<string, decimal>> TotalsAsync(DateOnly start, DateOnly end)
        {
            var rows = await c.QueryAsync("""
                SELECT COALESCE(NULLIF(it.category, ''), '') AS category, SUM(x.total) AS total FROM (
                    SELECT l.item_id, l.total FROM invoice_lines l JOIN invoices i ON i.id = l.invoice_id
                    WHERE i.status = 'issued' AND i.sale_id IS NULL AND i.issued_at >= $from AND i.issued_at < $to
                    UNION ALL
                    SELECT l.item_id, CASE n.kind WHEN 'debit' THEN l.total ELSE -l.total END FROM invoice_note_lines l
                    JOIN invoice_notes n ON n.id = l.note_id JOIN invoices i ON i.id = n.invoice_id
                    WHERE i.sale_id IS NULL AND n.issued_at >= $from AND n.issued_at < $to
                    UNION ALL
                    SELECT l.item_id, l.total FROM pos_sale_lines l JOIN pos_sales s ON s.id = l.sale_id
                    WHERE s.status = 'completed' AND s.created_at >= $from AND s.created_at < $to) x
                LEFT JOIN items it ON it.id = x.item_id
                GROUP BY 1
                """, r => (Category: r.Str("category"), Total: r.Dec("total")), null,
                ("$from", ToUtc(start)), ("$to", ToUtc(end.AddDays(1))));
            return rows.ToDictionary(r => r.Category, r => r.Total);
        }
        var current = await TotalsAsync(from, to);
        var previous = await TotalsAsync(prevFrom, from.AddDays(-1));
        return current.Keys.Union(previous.Keys)
            .Select(k => new CategoryRevenue(k, current.GetValueOrDefault(k), previous.GetValueOrDefault(k)))
            .Where(r => r.Current > 0 || r.Previous > 0)
            .OrderByDescending(r => r.Current).ThenBy(r => r.Category)
            .ToList();
    }

    /// <summary>Últimos comprobantes con su estado de cobro (pagado, parcial, pendiente o vencido).</summary>
    public async Task<IReadOnlyList<RecentTransaction>> GetRecentTransactionsAsync(string? search = null, int limit = 8,
        CancellationToken ct = default)
    {
        var today = Today();
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT i.id, i.number, i.kind, i.total, i.paid, i.status, i.credited, i.debited, i.due_date, i.issued_at, i.currency_code, p.name AS customer_name,
                   (SELECT description FROM invoice_lines WHERE invoice_id = i.id ORDER BY line_no LIMIT 1) AS first_line,
                   (SELECT COUNT(*) FROM invoice_lines WHERE invoice_id = i.id) AS lines,
                   (SELECT COALESCE(SUM(quantity), 0) FROM invoice_lines WHERE invoice_id = i.id) AS quantity
            FROM invoices i LEFT JOIN parties p ON p.id = i.customer_id
            WHERE $search IS NULL OR i.number LIKE $like OR p.name LIKE $like
            ORDER BY i.issued_at DESC LIMIT $limit
            """, r =>
            {
                var total = r.Dec("total");
                var paid = r.Dec("paid");
                var voided = r.Str("status") == InvoiceStatuses.Voided;
                var balance = voided ? 0 : total + r.Dec("debited") - r.Dec("credited") - paid;
                var due = r.StrOrNull("due_date") is { } d ? DateOnly.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : (DateOnly?)null;
                var status = voided ? TransactionStatuses.Voided
                    : balance <= 0 ? TransactionStatuses.Paid
                    : due < today ? TransactionStatuses.Overdue
                    : paid > 0 || balance < total ? TransactionStatuses.Partial
                    : TransactionStatuses.Pending;
                return new RecentTransaction(r.Guid("id"), r.Str("number"), r.Str("kind"), r.StrOrNull("customer_name"),
                    r.StrOrNull("first_line"), r.Int("lines"), r.Dec("quantity"), total, Math.Max(0, balance), status,
                    r.Time("issued_at"), r.Str("currency_code"));
            }, null, ("$search", Clean(search)), ("$like", Like(search)), ("$limit", limit));
    }

    private DateOnly Today() => LocalDay(Clock.GetUtcNow());

    private static DateOnly LocalDay(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(ErpClock.Offset).DateTime);

    private static DateTimeOffset ToUtc(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), ErpClock.Offset).ToUniversalTime();

    // Ingresos = comprobantes de pedidos (los de ventas de caja ya cuentan como venta) + ventas de caja netas de devoluciones.
    private static async Task<decimal> RevenueAsync(Microsoft.Data.Sqlite.SqliteConnection c, DateTimeOffset from, DateTimeOffset to) =>
        await InvoicedAsync(c, from, to) + await PosNetAsync(c, from, to);

    /// <summary>Notas de comprobantes de pedidos emitidas en el periodo: las de débito suman y las de crédito restan.</summary>
    private const string NotesSql = """
        SELECT n.issued_at, CASE n.kind WHEN 'debit' THEN n.total ELSE -n.total END AS total
        FROM invoice_notes n JOIN invoices i ON i.id = n.invoice_id
        WHERE i.sale_id IS NULL AND n.issued_at >= $from AND n.issued_at < $to
        """;

    /// <summary>
    /// Facturado en el periodo: comprobantes de pedidos vigentes (los anulados no cuentan) más las notas de débito y menos
    /// las de crédito emitidas en el periodo.
    /// </summary>
    private static Task<decimal> InvoicedAsync(Microsoft.Data.Sqlite.SqliteConnection c, DateTimeOffset from, DateTimeOffset to) =>
        c.ScalarAsync<decimal>($"""
            SELECT COALESCE(SUM(total), 0) FROM (
                SELECT issued_at, total FROM invoices
                WHERE status = 'issued' AND sale_id IS NULL AND issued_at >= $from AND issued_at < $to
                UNION ALL
                {NotesSql})
            """, null, ("$from", from), ("$to", to));

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
