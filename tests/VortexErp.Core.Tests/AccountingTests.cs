using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Services;

namespace VortexErp.Core.Tests;

/// <summary>Fase 5a: plan de cuentas, asientos automáticos y manuales, libros, estados financieros y cierre de periodo.</summary>
public sealed class AccountingTests : IAsyncLifetime
{
    // Perú (PCGE), lunes 2 de noviembre de 2026.
    private readonly TestWorkspace _t = new("EQPA");
    private static readonly DateOnly Hoy = new(2026, 11, 2);

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<Guid> InvoiceAsync(decimal qty)
    {
        var number = await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, qty)], "PEN");
        var order = (await _t.Erp.Orders.GetOrdersAsync(number, null, 1, 1)).Items.Single();
        var invoice = await _t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice);
        return (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery(invoice, null, null, null, 1, 1))).Items.Single().Id;
    }

    private async Task<Guid> SupplierInvoiceAsync(decimal qty, decimal cost, DateOnly issued)
    {
        var number = await _t.Erp.Purchases.CreateAsync(_t.Supplier, _t.Central, [new DocumentLine(_t.Coffee, qty, cost)], "PEN");
        var po = (await _t.Erp.Purchases.GetPurchasesAsync(number, null, 1, 1)).Items.Single().Id;
        await _t.Erp.SyncHandlers.HandleGoodsReceivedAsync(TestWorkspace.Event(SuiteEvents.GoodsReceived,
            new GoodsReceived(Guid.NewGuid(), "REC-1", po, _t.Central, [new QuantityLine(_t.Coffee, qty)]), source: "WMS"));
        return await _t.Erp.Payables.RegisterInvoiceAsync(new SupplierInvoiceDraft(_t.Supplier, po, $"F001-{qty}", issued, null, "PEN",
            [new SupplierInvoiceLine(_t.Coffee, qty, cost)]));
    }

    private static decimal Balance(IReadOnlyList<TrialBalanceRow> rows, string code) => rows.Where(r => r.Code == code).Sum(r => r.Balance);

    [Fact]
    public async Task Un_mes_con_ventas_cobros_ventas_de_caja_compras_y_pagos_cuadra_en_el_balance_de_comprobacion()
    {
        var invoice = await InvoiceAsync(10);                                     // 185 con IGV
        await _t.Erp.Invoices.RegisterPaymentAsync(invoice, 100m, PaymentMethods.Transfer, "OP-1");
        await _t.Erp.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted,
            new SaleCompleted(Guid.NewGuid(), "B-1", _t.Store, null, "PEN", 50m, 9m, 59m, [new SaleLine(_t.Coffee, "CAFE", 2, 29.5m, 0, 59m)], null), source: "POS") with { OccurredAt = _t.Clock.GetUtcNow() });
        var purchase = await SupplierInvoiceAsync(20, 5m, Hoy);                   // 100 + 18 de IGV
        await _t.Erp.Payables.RegisterPaymentAsync(purchase, 118m, Hoy, PaymentMethods.Transfer);

        var trial = await _t.Erp.Accounting.GetTrialBalanceAsync(Hoy);
        Assert.Equal(trial.Sum(r => r.Debit), trial.Sum(r => r.Credit));
        Assert.Equal(85m, Balance(trial, "1212"));                // por cobrar: 185 − 100
        Assert.Equal(-206.78m, Balance(trial, "70121"));          // ventas sin IGV: 156.78 + 50
        Assert.Equal(-37.22m, Balance(trial, "40111"));           // IGV de ventas: 28.22 + 9
        Assert.Equal(18m, Balance(trial, "40112"));               // crédito fiscal
        Assert.Equal(0m, Balance(trial, "4212"));                 // pagado entero
        Assert.Equal(59m, Balance(trial, "101"));                 // la venta de caja
        Assert.Equal(-18m, Balance(trial, "1041"));               // banco: +100 − 118

        var result = await _t.Erp.Accounting.GetIncomeStatementAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal((206.78m, 100m, 106.78m), (result.Income.Total, result.Expenses.Total, result.Result));
        Assert.True((await _t.Erp.Accounting.GetBalanceSheetAsync(Hoy)).Balanced);

        // Volver a contabilizar no duplica nada: cada documento tiene su asiento.
        Assert.Equal(0, await _t.Erp.Accounting.PostPendingAsync());
        var ledger = await _t.Erp.Accounting.GetLedgerAsync("1212", new DateOnly(2026, 11, 1), Hoy);
        Assert.Equal(85m, ledger[^1].Balance);
    }

    [Fact]
    public async Task Un_periodo_cerrado_no_acepta_documentos_con_fecha_anterior_y_reabrirlo_queda_en_la_auditoria()
    {
        await _t.Erp.Accounting.ClosePeriodAsync(new DateOnly(2026, 10, 31));
        await Assert.ThrowsAsync<ErpValidationException>(() => SupplierInvoiceAsync(5, 5m, new DateOnly(2026, 10, 30)));
        await SupplierInvoiceAsync(5, 5m, Hoy);
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Accounting.CreateEntryAsync(new DateOnly(2026, 10, 15), "Ajuste",
            [new JournalLine("6391", 10, 0), new JournalLine("1041", 0, 10)]));

        await _t.Erp.Accounting.ClosePeriodAsync(Hoy);
        var ex = await Assert.ThrowsAsync<ErpValidationException>(() => InvoiceAsync(1));
        Assert.Contains("02/11/2026", ex.Message);

        await _t.Erp.Accounting.ClosePeriodAsync(new DateOnly(2026, 10, 31));
        await InvoiceAsync(1);
        Assert.Contains((await _t.Erp.Audit.GetAsync(1, 50)).Items, a => a.Action == "accounting.period_reopened");
    }

    [Fact]
    public async Task El_asiento_manual_del_asistente_espera_la_aprobacion_y_se_corrige_con_otro_que_lo_revierte()
    {
        _t.Context.Role = ErpPermissions.AccountingAssistant;
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Accounting.CreateEntryAsync(Hoy, "Descuadrado",
            [new JournalLine("6391", 10, 0), new JournalLine("1041", 0, 9)]));
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Accounting.CreateEntryAsync(Hoy, "Cuenta que no existe",
            [new JournalLine("9999", 10, 0), new JournalLine("1041", 0, 10)]));
        var id = await _t.Erp.Accounting.CreateEntryAsync(Hoy, "Comisión de mantenimiento", [new JournalLine("6391", 12, 0), new JournalLine("1041", 0, 12)]);
        Assert.Single(await _t.Erp.Accounting.GetPendingEntriesAsync());
        Assert.Equal(0m, Balance(await _t.Erp.Accounting.GetTrialBalanceAsync(Hoy), "6391"));
        await Assert.ThrowsAnyAsync<ErpValidationException>(() => _t.Erp.Accounting.ApproveEntryAsync(id, true));

        _t.Context.Role = ErpPermissions.Accountant;
        await _t.Erp.Accounting.ApproveEntryAsync(id, true);
        Assert.Equal(12m, Balance(await _t.Erp.Accounting.GetTrialBalanceAsync(Hoy), "6391"));
        await _t.Erp.Accounting.ReverseEntryAsync(id, Hoy);
        var trial = await _t.Erp.Accounting.GetTrialBalanceAsync(Hoy);
        Assert.Equal((0m, 24m), (Balance(trial, "6391"), trial.Where(r => r.Code == "6391").Sum(r => r.Debit + r.Credit)));

        // El plan se puede ampliar (la primera vez se guarda el del país entero).
        await _t.Erp.Accounting.SaveAccountAsync(new LedgerAccount("6392", "Comisiones de tarjeta", AccountType.Expense));
        Assert.Contains(await _t.Erp.Accounting.GetAccountsAsync(), a => a.Code == "6392");
        Assert.Contains(await _t.Erp.Accounting.GetAccountsAsync(), a => a.Code == "1212");
    }
}
