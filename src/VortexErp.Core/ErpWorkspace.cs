using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Services;
using VortexErp.Core.Sync;

namespace VortexErp.Core;

/// <summary>
/// Todo el ERP de una organización: su base local y los servicios sobre ella. Cada organización tiene su propia base,
/// así un equipo compartido por dos empresas nunca mezcla datos.
/// </summary>
public sealed class ErpWorkspace
{
    private ErpWorkspace(ErpDatabase db, IErpContext context, TimeProvider clock)
    {
        Database = db;
        Master = new MasterDataService(db, context, clock);
        Orders = new OrderService(db, context, clock);
        Invoices = new InvoiceService(db, context, clock);
        Purchases = new PurchaseService(db, context, clock);
        Reports = new ReportService(db, context, clock);
        Audit = new AuditService(db, context, clock);
        SyncStore = new SqliteSyncStore(db);
        SyncHandlers = new ErpSyncHandlers(db, Master, clock);
    }

    public ErpDatabase Database { get; }
    public MasterDataService Master { get; }
    public OrderService Orders { get; }
    public InvoiceService Invoices { get; }
    public PurchaseService Purchases { get; }
    public ReportService Reports { get; }
    public AuditService Audit { get; }
    public SqliteSyncStore SyncStore { get; }
    public ErpSyncHandlers SyncHandlers { get; }

    public static async Task<ErpWorkspace> OpenAsync(string databasePath, IErpContext context, TimeProvider? clock = null,
        CancellationToken ct = default)
    {
        var db = new ErpDatabase(databasePath);
        await db.MigrateAsync(ct);
        return new ErpWorkspace(db, context, clock ?? TimeProvider.System);
    }
}
