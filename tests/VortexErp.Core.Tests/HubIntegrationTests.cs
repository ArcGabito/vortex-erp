using VortexApi.Client;
using VortexApi.Client.Sessions;
using VortexApi.Client.Sync;
using VortexErp.Core.Domain;
using VortexErp.Core.Sync;

namespace VortexErp.Core.Tests;

/// <summary>Solo corre con un hub real: <c>VORTEX_HUB_URL=http://localhost:5299/ dotnet test</c> (organización demo).</summary>
public sealed class HubFactAttribute : FactAttribute
{
    public HubFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VORTEX_HUB_URL")))
            Skip = "Define VORTEX_HUB_URL para probar contra un hub real.";
    }
}

public sealed class HubIntegrationTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [HubFact]
    public async Task Proveedor_y_orden_de_compra_sin_conexion_llegan_al_hub()
    {
        var client = new VortexApiClient(
            new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("VORTEX_HUB_URL")!) },
            new VortexApiClientOptions
            {
                ProductCode = ErpSyncHandlers.ProductCode,
                ProductKey = Environment.GetEnvironmentVariable("VORTEX_ERP_KEY") ?? "vxp_erp_dev_key_change_me",
            });
        var sessions = new VortexSessionManager(client, new InMemorySessionStore(), "vortex-erp-integration-tests");
        // Ana es OrgAdmin de la organización demo: tiene todos los permisos del ERP.
        await sessions.LoginAsync("ana@vortex.local", "Suite-Local-2026!", "Pruebas Vortex ERP", "tests");
        var sync = _t.Erp.SyncHandlers.Register(new VortexSync(client, sessions, _t.Erp.SyncStore, ErpSyncHandlers.SyncOptions));

        var first = await sync.SyncAsync();
        Assert.True(first.Completed, first.InterruptedBy);

        // Todo se registra sin red: un proveedor nuevo (dato del hub), un almacén y una orden de compra (dato del ERP).
        var tag = "20" + Random.Shared.Next(100_000_000, 999_999_999);
        var supplier = await _t.Erp.Master.CreatePartyAsync(new PartyDraft($"Proveedor de pruebas {tag}", "Company", false, true, "RUC", tag,
            null, null, "Lima"));
        var site = await _t.Erp.Master.CreateSiteAsync(new SiteDraft($"E{tag[^6..]}", "Almacén de pruebas", "Warehouse", null, null, "Lima", "PE"));
        var item = await _t.Erp.Master.CreateItemAsync(new ItemDraft($"ERP-{tag}", "Artículo de pruebas", "unidad", "Pruebas", null, 1, "PEN"));
        await _t.Erp.Purchases.CreateAsync(supplier, site, [new DocumentLine(item, 5, 0.80m)], "PEN");

        var second = await sync.SyncAsync();
        Assert.True(second.Completed, second.InterruptedBy);
        Assert.Equal(0, second.Failed);
        Assert.Equal(0, second.PendingOutbox);
        Assert.Equal(1, second.PushedEvents);         // purchase_order.created
        Assert.Equal(3, second.PushedMasterWrites);   // proveedor, almacén y artículo
    }
}
