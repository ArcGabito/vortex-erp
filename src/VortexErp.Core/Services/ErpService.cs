using Microsoft.Data.Sqlite;
using VortexApi.Suite.Auditing;
using VortexApi.Suite.Taxes;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;

using static VortexErp.Core.Texts.CoreText;

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

    /// <summary>Corta la operación si el rol del usuario no la permite (el mensaje se muestra tal cual).</summary>
    protected void RequirePermission(string permission)
    {
        if (!Context.Can(permission))
            throw new ErpValidationException(T("error.permiso_administrador", Context.Role, ErpPermissions.Describe(permission)));
    }

    /// <summary>Deja la acción en la bitácora, en la misma transacción que la acción.</summary>
    protected Task AuditAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string action, string entity, Guid? entityId,
        string? reference, string? detail) =>
        Audit.WriteAsync(c, tx, new AuditEntry(Guid.NewGuid(), now, Context.UserId, Context.UserName, Context.DeviceTag, action, entity,
            entityId?.ToString(), reference, detail));

    /// <summary>Régimen de impuesto de la empresa (país y tasa general del hub). Sin datos de la empresa, Perú.</summary>
    internal static async Task<TaxRegime> TaxRegimeAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        var company = await c.FirstOrDefaultAsync("SELECT country_code, default_tax_rate FROM company WHERE id = 1",
            r => new { Country = r.Str("country_code"), Rate = r.DecOrNull("default_tax_rate") }, tx);
        return TaxCatalog.For(company?.Country ?? "PE", company?.Rate);
    }

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
/// <summary>Bitácora de acciones sensibles (no se edita ni se borra).</summary>
internal static class Audit
{
    public static Task WriteAsync(SqliteConnection c, SqliteTransaction tx, AuditEntry e) =>
        c.ExecAsync("""
            INSERT INTO audit_log (id, at, user_id, user_name, device, action, entity, entity_id, reference, detail)
            VALUES ($id, $at, $user, $name, $device, $action, $entity, $entityId, $ref, $detail)
            """, tx, ("$id", e.Id), ("$at", e.At), ("$user", e.UserId), ("$name", e.UserName), ("$device", e.Device),
            ("$action", e.Action), ("$entity", e.Entity), ("$entityId", e.EntityId), ("$ref", e.Reference), ("$detail", e.Detail));
}

/// <summary>Consulta de la bitácora.</summary>
public sealed class AuditService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    public async Task<PagedResult<AuditRow>> GetAsync(int page, int pageSize, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.ViewAudit);
        await using var c = await Db.OpenAsync(ct);
        var total = await c.ScalarAsync<int>("SELECT COUNT(*) FROM audit_log");
        var rows = await c.QueryAsync("SELECT * FROM audit_log ORDER BY at DESC LIMIT $limit OFFSET $offset", r => new AuditRow(
            r.Guid("id"), r.Time("at"), r.StrOrNull("user_name"), r.StrOrNull("device"), r.Str("action"), r.Str("entity"),
            r.StrOrNull("reference"), r.StrOrNull("detail")), null, ("$limit", pageSize), ("$offset", (page - 1) * pageSize));
        return new PagedResult<AuditRow>(rows, total, page, pageSize);
    }
}
