using Microsoft.Extensions.Logging;
using VortexApi.Client;
using VortexApi.Client.Sessions;
using VortexApi.Client.Sync;
using VortexErp.Core;
using VortexErp.Core.Domain;
using VortexErp.Core.Sync;

namespace VortexErp.Services;

public enum ConnectionState
{
    /// <summary>Aún no se contactó al hub.</summary>
    Unknown,
    Online,

    /// <summary>Sin red, pero el offline token sigue vigente: se trabaja normalmente y se sincroniza al volver.</summary>
    Offline,

    /// <summary>Sin red y con el periodo sin conexión agotado: hay que conectarse.</summary>
    OfflineExpired,
}

public sealed record SyncStatus(
    bool IsSyncing, DateTimeOffset? LastSyncAt, int Pending, int Failed, string? LastError, VortexSyncResult? LastResult);

/// <summary>
/// Estado de la app: sesión con el hub (login, renovación, heartbeat, modo sin conexión), la base local de la
/// organización y la sincronización periódica. Los componentes se suscriben a <see cref="Changed"/> (estado) y
/// <see cref="DataChanged"/> (datos nuevos que recargar).
/// </summary>
public sealed class AppSession(HubSettings settings, ISessionStore sessionStore, ILogger<AppSession> logger) : IErpContext, IAsyncDisposable
{
    private const string ProductCode = ErpSyncHandlers.ProductCode;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _deviceId = DeviceIdentity.Get();
    private VortexApiClient? _client;
    private VortexSessionManager? _sessions;
    private VortexSync? _sync;
    private CancellationTokenSource? _loops;
    private SemaphoreSlim _wake = new(0);
    private DateTimeOffset? _lastSyncAt;
    private string? _lastError;
    private VortexSyncResult? _lastResult;
    private bool _syncing;
    private int _pending, _failed;

    public event Action? Changed;
    public event Action? DataChanged;

    public bool Initialized { get; private set; }
    public VortexSession? Session { get; private set; }
    public ErpWorkspace? Workspace { get; private set; }
    public ConnectionState Connection { get; private set; }
    public DateTimeOffset? OfflineValidUntil { get; private set; }

    /// <summary>Motivo del último cierre de sesión forzado (acceso retirado, equipo dado de baja…), para el login.</summary>
    public string? SignedOutReason { get; private set; }

    public bool IsSignedIn => Session is not null && Workspace is not null;
    public string HubUrl => settings.HubUrl;
    public string ProductKey => settings.ProductKey;
    public string DeviceId => _deviceId;
    public SyncStatus Sync => new(_syncing, _lastSyncAt, _pending, _failed, _lastError, _lastResult);

    // ---------- IErpContext ----------

    public Guid? UserId => Session?.User.Id;
    public string UserName => Session?.User.FullName ?? "";
    public string DeviceTag => DeviceIdentity.Tag(_deviceId);

    public Guid? DefaultSiteId =>
        Session is { } s && Preferences.Default.Get<string?>(DefaultSiteKey(s), null) is { } id && Guid.TryParse(id, out var g) ? g : null;

    public void SetDefaultSite(Guid? siteId)
    {
        if (Session is not { } s)
            return;
        if (siteId is { } id)
            Preferences.Default.Set(DefaultSiteKey(s), id.ToString());
        else
            Preferences.Default.Remove(DefaultSiteKey(s));
        Changed?.Invoke();
    }

    private static string DefaultSiteKey(VortexSession s) => $"vortex.default_site.{s.Access.OrganizationId:N}";

    // ---------- Ciclo de vida ----------

    /// <summary>Al abrir la app: si hay una sesión guardada, abre la base y arranca la sincronización (aun sin red).</summary>
    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Initialized)
                return;
            await settings.LoadAsync();
            BuildClient();
            if (await _sessions!.GetSessionAsync() is { } session)
                await StartAsync(session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo restaurar la sesión");
        }
        finally
        {
            Initialized = true;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Inicia sesión. Devuelve el mensaje de error para el usuario, o null si entró.</summary>
    public async Task<string?> LoginAsync(string email, string password, string hubUrl, string productKey)
    {
        if (!HubSettings.TryNormalize(hubUrl, out var url))
            return "La dirección del servidor no es válida (ej. https://hub.miempresa.pe).";
        if (string.IsNullOrWhiteSpace(productKey))
            return "Falta la clave de sistema del ERP.";

        await _gate.WaitAsync();
        try
        {
            await settings.SaveAsync(url, productKey);
            BuildClient();
            var session = await _sessions!.LoginAsync(email.Trim(), password, DeviceInfo.Current.Name,
                DeviceInfo.Current.Platform.ToString());
            SignedOutReason = null;
            await StartAsync(session);
            return null;
        }
        catch (VortexApiException ex)
        {
            return HubErrors.For(ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"No se pudo conectar con el hub en {url}. Revisa la dirección y tu conexión.";
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task SignOutAsync(string? reason = null)
    {
        await StopAsync();
        if (_sessions is not null)
            await _sessions.SignOutAsync();
        Session = null;
        Workspace = null;
        Connection = ConnectionState.Unknown;
        SignedOutReason = reason;
        Changed?.Invoke();
    }

    private void BuildClient()
    {
        if (_sessions is not null)
            _sessions.SessionEnded -= OnSessionEnded;
        _client = new VortexApiClient(new HttpClient { BaseAddress = new Uri(settings.HubUrl), Timeout = TimeSpan.FromSeconds(20) },
            new VortexApiClientOptions { ProductCode = ProductCode, ProductKey = settings.ProductKey });
        _sessions = new VortexSessionManager(_client, sessionStore, _deviceId);
        _sessions.SessionEnded += OnSessionEnded;
    }

    private async Task StartAsync(VortexSession session)
    {
        await StopAsync();
        Session = session;
        var path = Path.Combine(FileSystem.AppDataDirectory, $"vortex-erp-{session.Access.OrganizationId:N}.db");
        Workspace = await ErpWorkspace.OpenAsync(path, this);
        _sync = Workspace.SyncHandlers.Register(new VortexSync(_client!, _sessions!, Workspace.SyncStore, ErpSyncHandlers.SyncOptions));
        _pending = await Workspace.SyncStore.CountPendingAsync();
        _failed = (await Workspace.SyncStore.GetFailedAsync()).Count;
        _lastError = null;

        _loops = new CancellationTokenSource();
        _wake = new SemaphoreSlim(0);
        var ct = _loops.Token;
        _ = Task.Run(() => HeartbeatLoopAsync(ct), ct);
        _ = Task.Run(() => SyncLoopAsync(ct), ct);
    }

    private async Task StopAsync()
    {
        if (_loops is null)
            return;
        await _loops.CancelAsync();
        _loops.Dispose();
        _loops = null;
    }

    private void OnSessionEnded(object? sender, VortexApiException ex) =>
        _ = SignOutAsync(HubErrors.For(ex));

    // ---------- Heartbeat y modo sin conexión ----------

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(settings.HeartbeatInterval);
        do
        {
            await HeartbeatAsync(ct);
        } while (await WaitAsync(timer, ct));
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            var heartbeat = await _sessions!.HeartbeatAsync(ct);
            Session = await _sessions.GetSessionAsync(ct) ?? Session;
            OfflineValidUntil = heartbeat.OfflineTokenExpiresAt;
            SetConnection(ConnectionState.Online);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await GoOfflineAsync(ct);
        }
        catch (VortexApiException ex) when (ex.IsAccessDenied || ex.StatusCode == 401)
        {
            await SignOutAsync(HubErrors.For(ex));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Heartbeat");
        }
    }

    private async Task GoOfflineAsync(CancellationToken ct)
    {
        var offline = await _sessions!.ValidateOfflineAsync(settings.Issuer, ct);
        OfflineValidUntil = offline.Access?.ValidUntil;
        SetConnection(offline.IsValid ? ConnectionState.Offline : ConnectionState.OfflineExpired);
    }

    private void SetConnection(ConnectionState state)
    {
        if (Connection == state)
            return;
        Connection = state;
        Changed?.Invoke();
    }

    // ---------- Sincronización ----------

    /// <summary>Pide sincronizar ya (p. ej. después de registrar una operación).</summary>
    public void SyncNow()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    /// <summary>Avisa a las pantallas que cambiaron datos locales y sube los cambios en cuanto haya red.</summary>
    public void NotifyLocalChange()
    {
        _ = RefreshCountersAsync();
        DataChanged?.Invoke();
        SyncNow();
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SyncOnceAsync(ct);
            try
            {
                await _wake.WaitAsync(settings.SyncInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        if (_sync is null || Workspace is null)
            return;
        _syncing = true;
        Changed?.Invoke();
        var hadData = false;
        try
        {
            var result = await _sync.SyncAsync(ct);
            _lastResult = result;
            if (result.Completed)
            {
                _lastSyncAt = DateTimeOffset.Now;
                _lastError = null;
                SetConnection(ConnectionState.Online);
            }
            else
            {
                await GoOfflineAsync(ct);
            }
            hadData = result.PulledChanges + result.PulledEvents + result.Failed > 0;
        }
        catch (VortexSyncException ex)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Sincronización detenida en un cambio del feed");
        }
        catch (VortexApiException ex) when (ex.IsAccessDenied || ex.StatusCode == 401)
        {
            await SignOutAsync(HubErrors.For(ex));
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _lastError = ex is VortexApiException api ? HubErrors.For(api) : ex.Message;
            logger.LogError(ex, "Sincronización");
        }
        finally
        {
            _syncing = false;
        }

        await RefreshCountersAsync();
        if (hadData)
            DataChanged?.Invoke();
        Changed?.Invoke();
    }

    private async Task RefreshCountersAsync()
    {
        if (Workspace is not { } erp)
            return;
        try
        {
            _pending = await erp.SyncStore.CountPendingAsync();
            _failed = (await erp.SyncStore.GetFailedAsync()).Count;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Contadores de sincronización");
        }
    }

    public async Task<IReadOnlyList<VortexFailedEntry>> GetFailedAsync() =>
        Workspace is { } erp ? await erp.SyncStore.GetFailedAsync() : [];

    public async Task DismissFailedAsync(Guid id)
    {
        if (Workspace is not { } erp)
            return;
        await erp.SyncStore.DismissFailedAsync(id);
        await RefreshCountersAsync();
        DataChanged?.Invoke();
        Changed?.Invoke();
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
