using VortexApi.Client;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;

namespace VortexErp.Core.Tests;

/// <summary>Fase 5b: centros de costo, cierre anual con apertura y diferencia de cambio no realizada; ventas de caja con comprobante electrónico.</summary>
public sealed class AccountingCloseTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new("EQPB");
    private static readonly Guid Laptop = Guid.Parse("22222222-0000-0000-0000-0000000000aa");
    private static readonly DateOnly Hoy = new(2026, 11, 2);

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _t.Erp.Master.ApplyItemAsync(TestWorkspace.Item(Laptop, "LAP-01", "Laptop", 100m, currency: "USD"));
        await _t.Erp.Master.ApplyExchangeRateAsync(Rate(new DateOnly(2026, 11, 1), 3.75m, 3.76m));
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private static VortexMasterChange<VortexExchangeRate> Rate(DateOnly date, decimal rate, decimal? sell)
    {
        var key = Guid.NewGuid();
        return new(1, key, VortexChangeOperation.Created, 1, DateTimeOffset.UtcNow, "HUB",
            new VortexExchangeRate(key, date, "USD", "PEN", rate, null, sell, "SBS", new VortexMasterMeta(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));
    }

    private Task PosSaleAsync(decimal total, Guid site, SaleFiscal? fiscal = null, Guid? id = null) =>
        _t.Erp.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted,
            new SaleCompleted(id ?? Guid.NewGuid(), "V-1", site, null, "PEN", Math.Round(total / 1.18m, 2), total - Math.Round(total / 1.18m, 2), total,
                [new SaleLine(_t.Coffee, "CAFE", 1, total, 0, total)], null, fiscal), source: "POS") with { OccurredAt = _t.Clock.GetUtcNow() });

    [Fact]
    public async Task Los_ingresos_de_cada_sede_van_a_su_centro_de_costo_y_el_asiento_manual_elige_el_suyo()
    {
        await _t.Erp.Accounting.SaveCostCenterAsync(new CostCenter("tda-01", "Tienda Miraflores"));
        await _t.Erp.Accounting.SaveCostCenterAsync(new CostCenter("ADM", "Administración"));
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Accounting.SaveSiteCostCentersAsync(new Dictionary<Guid, string> { [_t.Store] = "NOEXISTE" }));
        await _t.Erp.Accounting.SaveSiteCostCentersAsync(new Dictionary<Guid, string> { [_t.Store] = "TDA-01" });

        await PosSaleAsync(118m, _t.Store);
        await PosSaleAsync(59m, _t.Central);
        await _t.Erp.Accounting.CreateEntryAsync(Hoy, "Alquiler de oficina", [new JournalLine("659", 40, 0, CostCenter: "adm"), new JournalLine("1041", 0, 40)]);
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Accounting.CreateEntryAsync(Hoy, "Centro inexistente",
            [new JournalLine("659", 1, 0, CostCenter: "X"), new JournalLine("1041", 0, 1)]));

        var results = await _t.Erp.Accounting.GetCostCenterResultsAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal([("TDA-01", 100m, 0m), ("ADM", 0m, 40m), (null, 50m, 0m)], results.OrderBy(r => r.Code == "ADM" ? 1 : r.Code is null ? 2 : 0)
            .Select(r => (r.Code, r.Income, r.Expense)));
        var store = await _t.Erp.Accounting.GetIncomeStatementAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), "TDA-01");
        Assert.Equal(100m, store.Result);
    }

    [Fact]
    public async Task El_cierre_anual_lleva_el_resultado_a_utilidades_y_el_ejercicio_siguiente_abre_con_los_saldos()
    {
        await PosSaleAsync(118m, _t.Store);                                  // 100 de venta + 18 de IGV
        await _t.Erp.Accounting.CreateEntryAsync(Hoy, "Comisión", [new JournalLine("6391", 30, 0), new JournalLine("1041", 0, 30)]);

        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Accounting.CloseYearAsync(2026));
        _t.Clock.Advance(TimeSpan.FromDays(62));                             // 3 de enero de 2027
        var close = await _t.Erp.Accounting.CloseYearAsync(2026);
        Assert.Equal(70m, close.Result);

        var end = new DateOnly(2026, 12, 31);
        var trial = await _t.Erp.Accounting.GetTrialBalanceAsync(end);
        Assert.Equal(0m, trial.Where(r => r.Type is AccountType.Income or AccountType.Expense).Sum(r => r.Balance));
        Assert.Equal(-70m, trial.Single(r => r.Code == "591").Balance);      // utilidades no distribuidas
        // El estado de resultados del año sigue mostrando la operación, no el cierre.
        Assert.Equal(70m, (await _t.Erp.Accounting.GetIncomeStatementAsync(new DateOnly(2026, 1, 1), end)).Result);
        var sheet = await _t.Erp.Accounting.GetBalanceSheetAsync(end);
        Assert.True(sheet.Balanced);
        Assert.Equal(0m, sheet.CurrentResult);
        Assert.Equal(new DateOnly(2026, 12, 31), (await _t.Erp.Accounting.GetSettingsAsync()).ClosedThrough);

        var opening = await _t.Erp.Accounting.GetOpeningEntryAsync(2027);
        Assert.True(opening.Balanced);
        Assert.Equal(new DateOnly(2027, 1, 1), opening.Date);
        Assert.Contains(opening.Lines, l => l.AccountCode == "101" && l.Debit == 118m);
        Assert.Contains(opening.Lines, l => l.AccountCode == "591" && l.Credit == 70m);

        // Repetirlo recalcula el mismo asiento (Id fijo por año): no duplica.
        await _t.Erp.Accounting.CloseYearAsync(2026);
        Assert.Single(await _t.Erp.Accounting.GetJournalAsync(end, end), e => e.Source == EntrySources.YearClose);
    }

    [Fact]
    public async Task La_diferencia_de_cambio_no_realizada_revalua_lo_pendiente_y_se_revierte_al_dia_siguiente()
    {
        await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(Laptop, 1)], "USD");
        var order = (await _t.Erp.Orders.GetOrdersAsync(null, "uninvoiced", 1, 10)).Items.First();
        await _t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice);   // US$ 100 a 3.76 = S/ 376
        await _t.Erp.Master.ApplyExchangeRateAsync(Rate(new DateOnly(2026, 11, 30), 3.85m, 3.86m));
        _t.Clock.Advance(TimeSpan.FromDays(29));

        var preview = await _t.Erp.Accounting.RevalueFxAsync(new DateOnly(2026, 11, 30), post: false);
        var line = Assert.Single(preview.Lines);
        Assert.Equal(("receivable", 100m, 3.76m), (line.Kind, line.Outstanding, line.BookedRate));
        Assert.Equal(Math.Round(100m * line.Rate, 2) - 376m, line.Adjustment);
        Assert.True(line.Adjustment > 0);
        Assert.DoesNotContain(await _t.Erp.Accounting.GetJournalAsync(new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 1)),
            e => e.Source == EntrySources.FxRevaluation);

        var posted = await _t.Erp.Accounting.RevalueFxAsync(new DateOnly(2026, 11, 30), post: true);
        Assert.Equal(line.Adjustment, posted.Result);
        var atMonthEnd = await _t.Erp.Accounting.GetTrialBalanceAsync(new DateOnly(2026, 11, 30));
        Assert.Equal(376m + line.Adjustment, atMonthEnd.Single(r => r.Code == "1212").Balance);
        Assert.Equal(-line.Adjustment, atMonthEnd.Single(r => r.Code == "7761").Balance);
        // Al día siguiente se revierte: la cuenta por cobrar vuelve al tipo de la emisión.
        Assert.Equal(376m, (await _t.Erp.Accounting.GetTrialBalanceAsync(new DateOnly(2026, 12, 1))).Single(r => r.Code == "1212").Balance);
        await _t.Erp.Accounting.RevalueFxAsync(new DateOnly(2026, 11, 30), post: true);
        Assert.Single(await _t.Erp.Accounting.GetJournalAsync(new DateOnly(2026, 11, 30), new DateOnly(2026, 11, 30)),
            e => e.Source == EntrySources.FxRevaluation);
    }

    [Fact]
    public async Task Una_venta_de_caja_con_comprobante_electronico_no_se_vuelve_a_facturar()
    {
        var withFiscal = Guid.NewGuid();
        var plain = Guid.NewGuid();
        await PosSaleAsync(59m, _t.Store, new SaleFiscal("receipt", "B001", 12), withFiscal);
        await PosSaleAsync(59m, _t.Store, id: plain);

        var pending = (await _t.Erp.Invoices.GetPosSalesAsync(true, 1, 20)).Items;
        Assert.Equal([plain], pending.Select(s => s.Id));
        var all = (await _t.Erp.Invoices.GetPosSalesAsync(false, 1, 20)).Items;
        Assert.Equal("B001-12", all.Single(s => s.Id == withFiscal).FiscalNumber);
        var ex = await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.IssueFromSaleAsync(withFiscal, InvoiceKinds.Receipt));
        Assert.Contains("B001-12", ex.Message);
    }
}
