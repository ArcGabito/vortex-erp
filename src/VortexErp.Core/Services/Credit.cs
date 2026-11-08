using Microsoft.Data.Sqlite;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;

namespace VortexErp.Core.Services;

/// <summary>
/// Crédito de un cliente en la moneda de la empresa: lo que debe (comprobantes con saldo) más los pedidos abiertos que
/// aún no se facturan. Los pedidos por aprobar no cuentan (todavía no son un compromiso).
/// </summary>
internal static class Credit
{
    public static async Task<(decimal Exposure, decimal Overdue)> ExposureAsync(SqliteConnection c, SqliteTransaction? tx, Guid customerId,
        DateOnly today)
    {
        var company = await Fx.CompanyCurrencyAsync(c, tx);
        var invoices = (await c.QueryAsync("""
            SELECT COALESCE(SUM((total + debited - credited - paid) * COALESCE(exchange_rate, 1)), 0) AS balance,
                   COALESCE(SUM(CASE WHEN due_date < $today THEN (total + debited - credited - paid) * COALESCE(exchange_rate, 1) ELSE 0 END), 0) AS overdue
            FROM invoices WHERE customer_id = $id AND status = 'issued' AND total + debited - credited - paid > 0
            """, r => (Balance: r.Dec("balance"), Overdue: r.Dec("overdue")), tx, ("$id", customerId), ("$today", today.ToString("yyyy-MM-dd")))).Single();

        var orders = await c.QueryAsync("""
            SELECT currency_code, SUM(total) AS total FROM sales_orders
            WHERE customer_id = $id AND status <> 'cancelled' AND invoice_id IS NULL AND COALESCE(approval, '') <> 'pending'
            GROUP BY currency_code
            """, r => (Currency: r.Str("currency_code"), Total: r.Dec("total")), tx, ("$id", customerId));
        var open = 0m;
        foreach (var o in orders)
            open += o.Total * (await Fx.RateAsync(c, tx, o.Currency, company, today) ?? 1m);
        return (Round(invoices.Balance + open), Round(invoices.Overdue));
    }

    public static async Task<decimal> ToCompanyAsync(SqliteConnection c, SqliteTransaction? tx, decimal amount, string currency, DateOnly today)
    {
        var company = await Fx.CompanyCurrencyAsync(c, tx);
        return Round(amount * (await Fx.RateAsync(c, tx, currency, company, today) ?? 1m));
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
