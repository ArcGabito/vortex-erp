using Microsoft.Extensions.Configuration;

namespace VortexErp.Services;

/// <summary>
/// Conexión al hub. Los valores por defecto vienen de <c>appsettings.json</c> (embebido); cada instalación puede
/// apuntar a su propio hub desde la pantalla de inicio de sesión. La URL se guarda en preferencias y la clave de
/// sistema en almacenamiento seguro: nunca quedan fijas en el código.
/// </summary>
public sealed class HubSettings
{
    private const string UrlKey = "vortex.hub_url";
    private const string ProductKeyKey = "vortex.product_key";

    private readonly IConfiguration _config;

    public HubSettings(IConfiguration config)
    {
        _config = config;
        HubUrl = Preferences.Default.Get<string?>(UrlKey, null) ?? DefaultHubUrl;
    }

    public string HubUrl { get; private set; }
    public string ProductKey { get; private set; } = "";
    public string Issuer => _config["Vortex:Issuer"] ?? "https://hub.vortex.local";
    public TimeSpan SyncInterval => TimeSpan.FromSeconds(_config.GetValue("Vortex:SyncIntervalSeconds", 30));
    public TimeSpan HeartbeatInterval => TimeSpan.FromMinutes(_config.GetValue("Vortex:HeartbeatMinutes", 5));

    /// <summary>En el emulador de Android, <c>localhost</c> es el propio emulador: el equipo anfitrión es 10.0.2.2.</summary>
    private string DefaultHubUrl =>
        DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.DeviceType == DeviceType.Virtual
            ? _config["Vortex:AndroidEmulatorHubUrl"] ?? "http://10.0.2.2:5299/"
            : _config["Vortex:HubUrl"] ?? "http://localhost:5299/";

    public async Task LoadAsync()
    {
        string? stored = null;
        try
        {
            stored = await SecureStorage.Default.GetAsync(ProductKeyKey);
        }
        catch (Exception)
        {
            // Almacenamiento seguro no disponible o dañado: se usa la clave por defecto.
        }
        ProductKey = stored ?? _config["Vortex:ProductKey"] ?? "";
    }

    public async Task SaveAsync(string hubUrl, string productKey)
    {
        HubUrl = Normalize(hubUrl);
        ProductKey = productKey.Trim();
        Preferences.Default.Set(UrlKey, HubUrl);
        await SecureStorage.Default.SetAsync(ProductKeyKey, ProductKey);
    }

    public static bool TryNormalize(string? url, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        normalized = Normalize(uri.ToString());
        return true;
    }

    private static string Normalize(string url) => url.Trim().TrimEnd('/') + "/";
}

/// <summary>Id del equipo: un GUID creado en la primera ejecución y guardado para siempre (no es un id de hardware).</summary>
public static class DeviceIdentity
{
    private const string Key = "vortex.device_id";

    public static string Get()
    {
        var id = Preferences.Default.Get<string?>(Key, null);
        if (id is { Length: >= 16 })
            return id;
        id = Guid.NewGuid().ToString();
        Preferences.Default.Set(Key, id);
        return id;
    }

    /// <summary>Prefijo corto para numerar documentos (<c>REC-3F0C-00012</c>).</summary>
    public static string Tag(string deviceId) => deviceId.Replace("-", "")[..4].ToUpperInvariant();
}
