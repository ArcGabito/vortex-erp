using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexErp.Core.Data;

namespace VortexErp.Core.Sync;

/// <summary>
/// Estado de <see cref="VortexSync"/> (cursores, cola de salida y rechazos) en la misma base SQLite que los datos
/// del ERP. Así una operación y sus eventos se guardan juntos en una sola transacción (ver <see cref="Outbox"/>).
/// </summary>
public sealed class SqliteSyncStore(ErpDatabase db) : IVortexSyncStore
{
    public async Task<long> GetCursorAsync(string stream, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ScalarAsync<long>("SELECT cursor FROM sync_cursors WHERE stream = $s", null, ("$s", stream));
    }

    public async Task SaveCursorAsync(string stream, long cursor, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecAsync("INSERT INTO sync_cursors (stream, cursor) VALUES ($s, $c) ON CONFLICT (stream) DO UPDATE SET cursor = $c",
            null, ("$s", stream), ("$c", cursor));
    }

    public async Task EnqueueAsync(VortexOutboxEntry entry, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await Outbox.InsertAsync(c, null, entry);
    }

    public async Task<IReadOnlyList<VortexOutboxEntry>> PeekAsync(int max, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QueryAsync("SELECT entry FROM sync_outbox ORDER BY seq LIMIT $max",
            r => Deserialize<VortexOutboxEntry>(r.Str("entry")), null, ("$max", max));
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await c.ExecAsync("DELETE FROM sync_outbox WHERE id = $id", null, ("$id", id));
    }

    public async Task FailAsync(Guid id, string errorCode, string error, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        await c.ExecAsync("""
            INSERT OR REPLACE INTO sync_failed (id, entry, error_code, error, failed_at)
            SELECT id, entry, $code, $error, $at FROM sync_outbox WHERE id = $id;
            DELETE FROM sync_outbox WHERE id = $id;
            """, tx, ("$id", id), ("$code", errorCode), ("$error", error), ("$at", at));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<VortexFailedEntry>> GetFailedAsync(CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.QueryAsync("SELECT * FROM sync_failed ORDER BY failed_at DESC", r => new VortexFailedEntry(
            Deserialize<VortexOutboxEntry>(r.Str("entry")), r.Str("error_code"), r.Str("error"), r.Time("failed_at")));
    }

    /// <summary>Operaciones pendientes de subir (para mostrar al usuario).</summary>
    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        return await c.ScalarAsync<int>("SELECT COUNT(*) FROM sync_outbox");
    }

    /// <summary>
    /// El usuario descarta un rechazo del hub. Si era el alta de un dato maestro que nunca llegó al hub, también se
    /// borra la copia local (no existe en ningún otro lado).
    /// </summary>
    public async Task DismissFailedAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var json = await c.ScalarAsync<string?>("SELECT entry FROM sync_failed WHERE id = $id", tx, ("$id", id));
        if (json is not null && Deserialize<VortexOutboxEntry>(json) is { Kind: VortexOutboxKind.MasterUpsert, EntityId: { } entityId } entry
            && MasterTable(entry.Resource) is { } table)
        {
            await c.ExecAsync($"DELETE FROM {table} WHERE id = $id AND version = 0", tx, ("$id", entityId));
            await c.ExecAsync($"UPDATE {table} SET pending = 0 WHERE id = $id", tx, ("$id", entityId));
        }
        await c.ExecAsync("DELETE FROM sync_failed WHERE id = $id", tx, ("$id", id));
        await tx.CommitAsync(ct);
    }

    internal static string? MasterTable(string? resource) => resource switch
    {
        VortexMasterResource.Items => "items",
        VortexMasterResource.Sites => "sites",
        VortexMasterResource.Parties => "parties",
        _ => null,
    };

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, VortexJson.Options)!;
}

/// <summary>
/// Encola operaciones para el hub dentro de una transacción del ERP. Replica lo que hacen
/// <see cref="VortexSync.EnqueueEventAsync"/> y <see cref="VortexSync.EnqueueUpsertAsync"/>, pero sin salir de la
/// transacción: si la operación local falla, tampoco queda el evento (y al revés).
/// </summary>
public static class Outbox
{
    public static Task<Guid> EnqueueEventAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string type,
        object data, string? subjectType = null, string? subjectId = null, Guid? id = null) =>
        InsertReturningIdAsync(c, tx, new VortexOutboxEntry(id ?? Guid.NewGuid(), VortexOutboxKind.Event, now, EventType: type,
            Payload: JsonSerializer.SerializeToElement(data, VortexJson.Options), SubjectType: subjectType, SubjectId: subjectId));

    public static Task EnqueueUpsertAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string resource, Guid id,
        object request, int? expectedVersion = null)
    {
        VortexMasterResource.EntityTypeOf(resource);
        return InsertAsync(c, tx, new VortexOutboxEntry(Guid.NewGuid(), VortexOutboxKind.MasterUpsert, now, Resource: resource,
            EntityId: id, ExpectedVersion: expectedVersion, Payload: JsonSerializer.SerializeToElement(request, VortexJson.Options)));
    }

    internal static Task InsertAsync(SqliteConnection c, SqliteTransaction? tx, VortexOutboxEntry entry) =>
        c.ExecAsync("INSERT OR IGNORE INTO sync_outbox (id, entry) VALUES ($id, $entry)", tx,
            ("$id", entry.Id), ("$entry", JsonSerializer.Serialize(entry, VortexJson.Options)));

    private static async Task<Guid> InsertReturningIdAsync(SqliteConnection c, SqliteTransaction tx, VortexOutboxEntry entry)
    {
        await InsertAsync(c, tx, entry);
        return entry.Id;
    }
}
