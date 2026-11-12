using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Services;

namespace VortexErp.Core.Tests;

/// <summary>Fase 6b: activos fijos con depreciación por país y presupuestos contra lo real.</summary>
public sealed class AssetsTests : IAsyncLifetime
{
    // Perú (PCGE), 2 de noviembre de 2026.
    private readonly TestWorkspace _t = new("EQPA");
    private static readonly DateOnly Hoy = new(2026, 11, 2);

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private static decimal Balance(IReadOnlyList<TrialBalanceRow> rows, string code) => rows.Where(r => r.Code == code).Sum(r => r.Balance);

    private static FixedAsset Laptop(int life = 48) =>
        new(Guid.Empty, "ac-01", "Laptop gerencia", AssetCategories.Computers, new DateOnly(2026, 1, 15), 3000m, 0m, life, CostCenter: "ADM");

    [Fact]
    public async Task La_laptop_se_deprecia_al_25_por_ciento_cada_mes_completo_y_su_baja_da_resultado()
    {
        Assert.Equal(48, await _t.Erp.Assets.SuggestedLifeAsync(AssetCategories.Computers));
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Assets.SaveAssetAsync(Laptop(life: 24)));   // más rápido que el 25 % de SUNAT
        var id = await _t.Erp.Assets.SaveAssetAsync(Laptop());

        var row = Assert.Single(await _t.Erp.Assets.GetAssetsAsync(Hoy));
        Assert.Equal(("AC-01", 562.50m, 2437.50m, 62.50m), (row.Asset.Code, row.Accumulated, row.BookValue, row.MonthlyAmount));   // feb–oct: 9 × 62.50

        await _t.Erp.Accounting.PostPendingAsync();
        var trial = await _t.Erp.Accounting.GetTrialBalanceAsync(Hoy);
        Assert.Equal((3000m, -562.50m, 562.50m, -3000m), (Balance(trial, "3361"), Balance(trial, "3913"), Balance(trial, "6814"), Balance(trial, "6011")));
        Assert.Contains(await _t.Erp.Accounting.GetCostCenterResultsAsync(new(2026, 1, 1), Hoy), r => r.Code == "ADM" && r.Expense == 562.50m);

        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Assets.SaveAssetAsync(Laptop() with { Id = id, Cost = 3500m }));
        await _t.Erp.Assets.SaveAssetAsync(Laptop() with { Id = id, Name = "Laptop de gerencia" });   // el nombre sí

        // Se vende hoy en 2600: el valor en libros es 2437.50, gana 162.50.
        await _t.Erp.Assets.DisposeAssetAsync(id, Hoy, 2600m);
        await _t.Erp.Accounting.PostPendingAsync();
        trial = await _t.Erp.Accounting.GetTrialBalanceAsync(Hoy);
        Assert.Equal((0m, 0m, -162.50m), (Balance(trial, "3361"), Balance(trial, "3913"), Balance(trial, "6551")));
        Assert.Equal(0m, Assert.Single(await _t.Erp.Assets.GetAssetsAsync(Hoy)).BookValue);
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Assets.DisposeAssetAsync(id, Hoy, 0));
    }

    [Fact]
    public async Task El_presupuesto_se_compara_con_lo_real_y_viaja_a_otro_equipo()
    {
        await _t.Erp.Assets.SaveAssetAsync(Laptop());
        await _t.Erp.Assets.SaveBudgetAsync(2026, "6814", null, [0, 60, 60, 60, 60, 60, 60, 60, 60, 60, 60, 60]);
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Assets.SaveBudgetAsync(2026, "3361", null, Enumerable.Repeat(1m, 12).ToList()));

        var lines = await _t.Erp.Assets.CompareAsync(2026, 2, 10);
        var dep = lines.Single(l => l.Code == "6814");
        Assert.Equal((540m, 562.50m, 22.50m, false), (dep.Budget, dep.Actual, dep.Variance, dep.Favorable));

        var other = new TestWorkspace("EQPB");
        await other.InitializeAsync();
        try
        {
            foreach (var e in (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.RecordUpserted))
                await other.Erp.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(e));
            Assert.Single(await other.Erp.Assets.GetAssetsAsync());
            Assert.Equal(660m, Assert.Single(await other.Erp.Assets.GetBudgetsAsync(2026)).Total);
        }
        finally
        {
            await other.DisposeAsync();
        }
    }
}
