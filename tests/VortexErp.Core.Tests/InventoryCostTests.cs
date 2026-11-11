using VortexErp.Core.Domain;
using VortexErp.Core.Integration;

namespace VortexErp.Core.Tests;

/// <summary>Fase 6a: inventario valorizado, costo de ventas y asientos de inventario con el costo promedio del WMS.</summary>
public sealed class InventoryCostTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new("EQPA");
    private static readonly DateOnly Hoy = new(2026, 11, 2);

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private Task MoveAsync(decimal delta, decimal onHand, string reason, decimal unitCost, Guid? site = null) =>
        _t.Erp.SyncHandlers.HandleStockAdjustedAsync(TestWorkspace.Event(SuiteEvents.StockAdjusted,
            new StockAdjusted(site ?? _t.Central, _t.Coffee, delta, onHand, reason, null, unitCost, delta * unitCost), source: "WMS")
            with { OccurredAt = _t.Clock.GetUtcNow() });

    private static decimal Balance(IReadOnlyList<TrialBalanceRow> rows, string code) => rows.Where(r => r.Code == code).Sum(r => r.Balance);

    [Fact]
    public async Task Entradas_ventas_y_mermas_del_wms_van_al_inventario_al_costo_de_ventas_y_a_mermas()
    {
        await MoveAsync(10, 10, "receipt", 6m);       // entra 60
        await MoveAsync(-4, 6, "sale", 6m);           // costo de ventas 24
        await MoveAsync(-1, 5, "damage", 6m);         // merma 6
        await MoveAsync(-2, 3, "transfer", 6m);       // traslado: no cambia el total
        await MoveAsync(2, 2, "transfer", 6m, _t.Store);

        Assert.True(await _t.Erp.Accounting.PostPendingAsync() > 0);
        Assert.Equal(0, await _t.Erp.Accounting.PostPendingAsync());   // no se contabiliza dos veces

        var trial = await _t.Erp.Accounting.GetTrialBalanceAsync(Hoy);
        Assert.Equal(30m, Balance(trial, "20111"));    // 60 − 24 − 6
        Assert.Equal(24m, Balance(trial, "6911"));
        Assert.Equal(6m, Balance(trial, "6593"));
        Assert.Equal(-60m, Balance(trial, "6111"));
        Assert.Contains((await _t.Erp.Accounting.GetAccountsAsync()), a => a.Code == "6911");

        // Lo que llega después el mismo día entra en otro asiento.
        await MoveAsync(-1, 2, "sale", 6m);
        Assert.Equal(1, await _t.Erp.Accounting.PostPendingAsync());
        Assert.Equal(30m, Balance(await _t.Erp.Accounting.GetTrialBalanceAsync(Hoy), "6911"));

        var stock = (await _t.Erp.Reports.GetStockAsync(null, _t.Central, 1, 10)).Items.Single();
        Assert.Equal((2m, 6m), (stock.OnHand, stock.UnitCost));
    }

    [Fact]
    public async Task Con_el_plan_guardado_las_cuentas_nuevas_del_plan_base_tambien_rigen()
    {
        await _t.Erp.Accounting.SaveAccountAsync(new LedgerAccount("659901", "Otra cuenta", AccountType.Expense));
        await MoveAsync(5, 5, "receipt", 2m);
        await MoveAsync(-5, 0, "sale", 2m);
        await _t.Erp.Accounting.PostPendingAsync();
        var result = await _t.Erp.Accounting.GetIncomeStatementAsync(Hoy, Hoy);
        Assert.Contains(result.Expenses.Accounts, a => a.Code == "6911" && a.Amount == 10m);
    }
}
