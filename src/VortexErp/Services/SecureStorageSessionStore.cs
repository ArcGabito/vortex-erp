using System.Text.Json;
using VortexApi.Client;
using VortexApi.Client.Sessions;

namespace VortexErp.Services;

/// <summary>Sesión del hub (tokens y llaves para validar sin conexión) en el almacenamiento seguro del sistema.</summary>
public sealed class SecureStorageSessionStore : ISessionStore
{
    private const string Key = "vortex.session";

    public async Task<VortexStoredSession?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(Key) is { } json
                ? JsonSerializer.Deserialize<VortexStoredSession>(json, VortexJson.Options)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // Sesión ilegible (p. ej. restaurada de otro equipo): se descarta y se pide iniciar sesión.
            SecureStorage.Default.Remove(Key);
            return null;
        }
    }

    public Task SaveAsync(VortexStoredSession session, CancellationToken ct = default) =>
        SecureStorage.Default.SetAsync(Key, JsonSerializer.Serialize(session, VortexJson.Options));

    public Task ClearAsync(CancellationToken ct = default)
    {
        SecureStorage.Default.Remove(Key);
        return Task.CompletedTask;
    }
}
