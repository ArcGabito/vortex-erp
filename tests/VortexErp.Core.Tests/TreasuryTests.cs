using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Services;

namespace VortexErp.Core.Tests;

/// <summary>Fase 4b: cuentas y cajas, transferencias, extractos con conciliación, depósitos del POS y flujo de caja.</summary>
public sealed class TreasuryTests : IAsyncLifetime
{
    // Reloj: lunes 2 de noviembre de 2026, 10:00 en Lima.
    private readonly TestWorkspace _a = new("EQPA");
    private readonly TestWorkspace _b = new("EQPB");
    private Guid _bank, _cash;

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _b.InitializeAsync();
        _bank = await _a.Erp.Treasury.SaveAccountAsync(new TreasuryAccount(Guid.Empty, "BCP soles", TreasuryAccountKinds.Bank, "BCP", "193-1234567-0-12",
            "PEN", 1000m, new DateOnly(2026, 11, 1), [PaymentMethods.Transfer, PaymentMethods.Card]));
        _cash = await _a.Erp.Treasury.SaveAccountAsync(new TreasuryAccount(Guid.Empty, "Caja principal", TreasuryAccountKinds.Cash, null, null,
            "PEN", 0m, new DateOnly(2026, 11, 1), [PaymentMethods.Cash]));
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private async Task<Guid> InvoiceAsync(decimal qty, int creditDays = 0)
    {
        var number = await _a.Erp.Orders.CreateAsync(_a.Customer, null, [new DocumentLine(_a.Coffee, qty)], "PEN");
        var order = (await _a.Erp.Orders.GetOrdersAsync(number, null, 1, 1)).Items.Single();
        var invoice = await _a.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice, creditDays);
        return (await _a.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery(invoice, null, null, null, 1, 1))).Items.Single().Id;
    }

    private async Task<Guid> SupplierInvoiceAsync(decimal qty, decimal cost, DateOnly due)
    {
        var number = await _a.Erp.Purchases.CreateAsync(_a.Supplier, _a.Central, [new DocumentLine(_a.Coffee, qty, cost)], "PEN");
        var po = (await _a.Erp.Purchases.GetPurchasesAsync(number, null, 1, 1)).Items.Single().Id;
        await _a.Erp.SyncHandlers.HandleGoodsReceivedAsync(TestWorkspace.Event(SuiteEvents.GoodsReceived,
            new GoodsReceived(Guid.NewGuid(), "REC-1", po, _a.Central, [new QuantityLine(_a.Coffee, qty)]), source: "WMS"));
        return await _a.Erp.Payables.RegisterInvoiceAsync(new SupplierInvoiceDraft(_a.Supplier, po, $"F001-{qty}", new DateOnly(2026, 11, 2), due,
            "PEN", [new SupplierInvoiceLine(_a.Coffee, qty, cost)]));
    }

    private async Task<decimal> BalanceAsync(Guid account) =>
        (await _a.Erp.Treasury.GetAccountsAsync()).Single(a => a.Account.Id == account).Balance;

    [Fact]
    public async Task Cobros_pagos_y_transferencias_mueven_la_cuenta_de_su_medio_de_pago()
    {
        var invoice = await InvoiceAsync(10);   // 185.00
        await _a.Erp.Invoices.RegisterPaymentAsync(invoice, 100m, PaymentMethods.Transfer, "OP-5521");
        await _a.Erp.Invoices.RegisterPaymentAsync(invoice, 85m, PaymentMethods.Cash, null);
        var supplierInvoice = await SupplierInvoiceAsync(4, 5m, new DateOnly(2026, 11, 20));
        await _a.Erp.Payables.RegisterPaymentAsync(supplierInvoice, 20m, new DateOnly(2026, 11, 3), PaymentMethods.Cash);

        Assert.Equal((1100m, 65m), (await BalanceAsync(_bank), await BalanceAsync(_cash)));
        await _a.Erp.Treasury.TransferAsync(_cash, _bank, 60m, new DateOnly(2026, 11, 3), reference: "DEP-1");
        Assert.Equal((1160m, 5m), (await BalanceAsync(_bank), await BalanceAsync(_cash)));
        var movements = await _a.Erp.Treasury.GetMovementsAsync(_bank);
        Assert.Equal([TreasuryMovementKinds.Transfer, TreasuryMovementKinds.Collection], movements.Select(m => m.Kind));

        // Una transferencia a otra moneda pide cuánto entra; un medio de pago marcado en otra cuenta deja de estar en la primera.
        var usd = await _a.Erp.Treasury.SaveAccountAsync(new TreasuryAccount(Guid.Empty, "BCP dólares", TreasuryAccountKinds.Bank, "BCP", null, "USD",
            0m, new DateOnly(2026, 11, 1), [PaymentMethods.Transfer]));
        await Assert.ThrowsAsync<ErpValidationException>(() => _a.Erp.Treasury.TransferAsync(_bank, usd, 370m, new DateOnly(2026, 11, 3)));
        await _a.Erp.Treasury.TransferAsync(_bank, usd, 370m, new DateOnly(2026, 11, 3), receivedAmount: 100m);
        Assert.Equal(100m, await BalanceAsync(usd));

        // Las cuentas y los movimientos llegan al otro equipo.
        foreach (var e in (await _a.OutboxAsync()).Where(e => e.EventType is SuiteEvents.RecordUpserted or SuiteEvents.PaymentReceived))
            if (e.EventType == SuiteEvents.RecordUpserted)
                await _b.Erp.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(e));
        Assert.Equal(3, (await _b.Erp.Treasury.GetAccountsAsync()).Count);

        _a.Context.Role = ErpPermissions.Billing;
        await Assert.ThrowsAsync<ErpValidationException>(() => _a.Erp.Treasury.TransferAsync(_cash, _bank, 1m, new DateOnly(2026, 11, 3)));
    }

    [Fact]
    public async Task El_extracto_se_concilia_solo_y_deja_a_la_vista_lo_que_no_tiene_pareja()
    {
        var invoice = await InvoiceAsync(10);
        await _a.Erp.Invoices.RegisterPaymentAsync(invoice, 100m, PaymentMethods.Transfer, "OP-5521");
        await _a.Erp.Treasury.TransferAsync(_bank, _cash, 50m, new DateOnly(2026, 11, 3));

        const string csv = """
            Banco de Crédito del Perú;Cuenta 193-1234567-0-12
            Fecha;Descripción;Referencia;Cargo;Abono
            03/11/2026;ABONO TRANSF CAFE ANDINO;OP-5521;;100,00
            03/11/2026;RETIRO VENTANILLA;;50,00;
            04/11/2026;COMISION MANTENIMIENTO;;5,50;
            05/11/2026;DEPOSITO EFECTIVO;7788;;300,00
            """;
        var result = await _a.Erp.Treasury.ImportStatementAsync(_bank, csv);
        Assert.Equal(new StatementImport(4, 0, 2, 2), result);
        var accounts = await _a.Erp.Treasury.GetAccountsAsync();
        Assert.Equal((0, 2), (accounts.Single(a => a.Account.Id == _bank).Unreconciled, accounts.Single(a => a.Account.Id == _bank).UnmatchedLines));

        // Importarlo otra vez no duplica nada.
        Assert.Equal(new StatementImport(0, 4, 0, 2), await _a.Erp.Treasury.ImportStatementAsync(_bank, csv));

        // La comisión se registra desde la línea; el depósito se concilia a mano con el cobro que llega después.
        var lines = await _a.Erp.Treasury.GetStatementLinesAsync(_bank, unmatchedOnly: true);
        var fee = lines.Single(l => l.Amount == -5.50m);
        await _a.Erp.Treasury.CreateFromLineAsync(fee.Id, TreasuryMovementKinds.Fee);
        Assert.Equal(1000m + 100m - 50m - 5.50m, await BalanceAsync(_bank));

        var deposit = lines.Single(l => l.Amount == 300m);
        Assert.Empty(await _a.Erp.Treasury.SuggestAsync(deposit.Id));
        var other = await InvoiceAsync(20);   // 370.00
        await _a.Erp.Invoices.RegisterPaymentAsync(other, 300m, PaymentMethods.Transfer, null);
        var suggestion = Assert.Single(await _a.Erp.Treasury.SuggestAsync(deposit.Id));
        await _a.Erp.Treasury.MatchAsync(deposit.Id, suggestion.Ref);
        Assert.Empty(await _a.Erp.Treasury.GetStatementLinesAsync(_bank, unmatchedOnly: true));
        await Assert.ThrowsAsync<ErpValidationException>(() => _a.Erp.Treasury.MatchAsync(fee.Id, suggestion.Ref));

        await _a.Erp.Treasury.UnmatchAsync(deposit.Id);
        Assert.Single(await _a.Erp.Treasury.GetStatementLinesAsync(_bank, unmatchedOnly: true));
    }

    [Fact]
    public void Lee_extractos_OFX_y_CAMT_053()
    {
        const string ofx = """
            OFXHEADER:100
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>
            <STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20261103120000<TRNAMT>1,250.40<FITID>A1<NAME>Pago cliente
            <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20261104<TRNAMT>-12.00<FITID>A2<MEMO>Comision
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;
        Assert.Equal([new BankEntry(new DateOnly(2026, 11, 3), 1250.40m, "Pago cliente", "A1"), new BankEntry(new DateOnly(2026, 11, 4), -12m, "Comision", "A2")],
            BankStatements.Parse(ofx));

        const string camt = """
            <?xml version="1.0"?>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02"><BkToCstmrStmt><Stmt>
              <Ntry><Amt Ccy="EUR">99.90</Amt><CdtDbtInd>CRDT</CdtDbtInd><BookgDt><Dt>2026-11-05</Dt></BookgDt><AcctSvcrRef>R-77</AcctSvcrRef>
                <NtryDtls><TxDtls><RmtInf><Ustrd>Factura F001-12</Ustrd></RmtInf></TxDtls></NtryDtls></Ntry>
              <Ntry><Amt Ccy="EUR">4.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><BookgDt><Dt>2026-11-06</Dt></BookgDt><AddtlNtryInf>Fee</AddtlNtryInf></Ntry>
            </Stmt></BkToCstmrStmt></Document>
            """;
        Assert.Equal([new BankEntry(new DateOnly(2026, 11, 5), 99.90m, "Factura F001-12", "R-77"), new BankEntry(new DateOnly(2026, 11, 6), -4m, "Fee", null)],
            BankStatements.Parse(camt));

        Assert.Equal([-1234.5m, 1234.5m, 1234.5m, -12m], new[] { "(1.234,50)", "1,234.50", "S/ 1.234,50", "-12" }.Select(BankStatements.ParseAmount));
    }

    [Fact]
    public async Task El_deposito_de_una_caja_del_POS_pasa_de_caja_al_banco_una_sola_vez()
    {
        var deposit = TestWorkspace.Event(SuiteEvents.CashDeposited,
            new CashDeposited(Guid.NewGuid(), _a.Central, 450m, "PEN", "OP-901", "BCP", new DateOnly(2026, 11, 3), null, "Rosa"), source: "POS");
        await _a.Erp.SyncHandlers.HandleCashDepositedAsync(deposit);
        await _a.Erp.SyncHandlers.HandleCashDepositedAsync(deposit with { Id = Guid.NewGuid() });   // reenviado: mismos movimientos
        Assert.Equal((1450m, -450m), (await BalanceAsync(_bank), await BalanceAsync(_cash)));

        // Y se concilia con el extracto por importe y referencia.
        var result = await _a.Erp.Treasury.ImportStatementAsync(_bank, "Fecha,Descripción,Importe,Referencia\n2026-11-04,DEPOSITO EN EFECTIVO,450.00,OP-901\n");
        Assert.Equal(1, result.Matched);
    }

    [Fact]
    public async Task El_flujo_proyectado_suma_lo_por_cobrar_y_resta_lo_por_pagar_por_semana()
    {
        await InvoiceAsync(10, creditDays: 10);                                       // 185 vence el jueves 12
        var supplier = await SupplierInvoiceAsync(20, 5m, new DateOnly(2026, 11, 20));   // 118 con IGV, vence el viernes 20
        await _a.Erp.Payables.PlanPaymentAsync(supplier, new DateOnly(2026, 11, 25));    // se pagará el 25

        var flow = await _a.Erp.Treasury.GetCashFlowAsync("PEN", 4);
        Assert.Equal(1000m, flow.Opening);
        Assert.Equal([new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23)],
            flow.Weeks.Select(w => w.Start));
        Assert.Equal([(0m, 0m, 1000m), (185m, 0m, 1185m), (0m, 0m, 1185m), (0m, 118m, 1067m)], flow.Weeks.Select(w => (w.In, w.Out, w.Balance)));
    }
}
