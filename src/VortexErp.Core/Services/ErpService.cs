using Microsoft.Data.Sqlite;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;

namespace VortexErp.Core.Services;

/// <summary>Base de los servicios: base local, contexto del usuario y reloj.</summary>
public abstract class ErpService(ErpDatabase db, IErpContext context, TimeProvider clock)
{
    protected ErpDatabase Db { get; } = db;
    protected IErpContext Context { get; } = context;
    protected TimeProvider Clock { get; } = clock;

    protected async Task<T> InTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, DateTimeOffset, Task<T>> work,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var result = await work(c, tx, Clock.GetUtcNow());
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Sede por defecto: la elegida en este equipo o el primer almacén activo.</summary>
    protected async Task<Guid?> DefaultSiteAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        if (Context.DefaultSiteId is { } chosen)
            return chosen;
        return await c.ScalarAsync<string?>("""
            SELECT id FROM sites WHERE is_deleted = 0 AND is_active = 1
            ORDER BY CASE kind WHEN 'Warehouse' THEN 0 ELSE 1 END, code LIMIT 1
            """, tx) is { } id ? Guid.Parse(id) : null;
    }

    /// <summary>Inicio del día local (Perú, UTC-5) como instante UTC, para filtrar "hoy".</summary>
    protected DateTimeOffset StartOfDay(int daysAgo = 0)
    {
        var local = Clock.GetUtcNow().ToOffset(ErpClock.Offset);
        return new DateTimeOffset(local.Date, ErpClock.Offset).AddDays(-daysAgo).ToUniversalTime();
    }

    protected static string Like(string? term) => $"%{term?.Trim().Replace("%", "").Replace("_", "")}%";

    protected static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    protected static void Require(bool condition, string message)
    {
        if (!condition)
            throw new ErpValidationException(message);
    }
}

/// <summary>Zona horaria de los reportes y vencimientos. Perú no usa horario de verano.</summary>
public static class ErpClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-5);
}

internal static class DocumentNumbers
{
    public const string Order = "PED";
    public const string Payment = "COB";
    public const string Purchase = "OC";

    /// <summary>Numeración por equipo (<c>PED-3F0C-000012</c>): dos equipos sin conexión nunca generan el mismo número.</summary>
    public static async Task<string> NextAsync(SqliteConnection c, SqliteTransaction tx, string prefix, string deviceTag) =>
        $"{prefix}-{deviceTag}-{await NextValueAsync(c, tx, prefix):D6}";

    /// <summary>
    /// Comprobante con serie de 4 caracteres al estilo SUNAT: la letra del tipo (F factura, B boleta) y 3 caracteres del
    /// equipo (<c>F3F0-000045</c>), así cada equipo tiene su propia serie y la numeración nunca choca.
    /// </summary>
    public static async Task<string> NextInvoiceAsync(SqliteConnection c, SqliteTransaction tx, string kind, string deviceTag)
    {
        var series = (kind == InvoiceKinds.Invoice ? "F" : "B") + deviceTag[..3];
        return $"{series}-{await NextValueAsync(c, tx, series):D6}";
    }

    private static Task<long> NextValueAsync(SqliteConnection c, SqliteTransaction tx, string prefix) =>
        c.ScalarAsync<long>("""
            INSERT INTO document_sequences (prefix, value) VALUES ($p, 1)
            ON CONFLICT (prefix) DO UPDATE SET value = value + 1 RETURNING value
            """, tx, ("$p", prefix));
}
