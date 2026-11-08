using Microsoft.Data.Sqlite;
using VortexErp.Core.Data;

namespace VortexErp.Core.Services;

/// <summary>
/// Tipos de cambio de la copia local del hub (<c>exchange_rate</c>). Se usa el del día pedido o el último anterior; para
/// documentos de venta, el de venta si el hub lo trae (si no, el de referencia). Un par sin dato directo se calcula con el
/// inverso del par contrario.
/// </summary>
public static class Fx
{
    /// <summary>Cuánto vale 1 <paramref name="from"/> en <paramref name="to"/> en <paramref name="date"/>; null si no hay dato.</summary>
    public static async Task<decimal?> RateAsync(SqliteConnection c, SqliteTransaction? tx, string from, string to, DateOnly date)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return 1m;
        var day = date.ToString("yyyy-MM-dd");
        var direct = await c.ScalarAsync<decimal?>("""
            SELECT COALESCE(sell_rate, rate) FROM exchange_rates
            WHERE is_deleted = 0 AND from_currency = $f AND to_currency = $t AND date <= $d ORDER BY date DESC LIMIT 1
            """, tx, ("$f", from.ToUpperInvariant()), ("$t", to.ToUpperInvariant()), ("$d", day));
        if (direct is > 0)
            return direct;
        var inverse = await c.ScalarAsync<decimal?>("""
            SELECT rate FROM exchange_rates
            WHERE is_deleted = 0 AND from_currency = $t AND to_currency = $f AND date <= $d ORDER BY date DESC LIMIT 1
            """, tx, ("$f", from.ToUpperInvariant()), ("$t", to.ToUpperInvariant()), ("$d", day));
        return inverse is > 0 ? Math.Round(1m / inverse.Value, 6) : null;
    }

    /// <summary>Moneda de la empresa (la del hub; sin datos de la empresa, soles).</summary>
    public static async Task<string> CompanyCurrencyAsync(SqliteConnection c, SqliteTransaction? tx) =>
        await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1", tx) ?? "PEN";
}
