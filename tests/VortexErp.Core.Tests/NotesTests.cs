using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Pdf;

namespace VortexErp.Core.Tests;

/// <summary>Fase 2 de la hoja de ruta: notas de crédito y débito, anulación, antigüedad de saldos, estado de cuenta y sedes
/// por usuario.</summary>
public sealed class NotesTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    /// <summary>Factura de 10 cafés (185.00 con IGV) y 20 panes exonerados (8.00): 193.00.</summary>
    private async Task<InvoiceRow> InvoiceAsync(int creditDays = 30)
    {
        await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 10), new DocumentLine(_t.Bread, 20, 0.40m)], "PEN");
        var order = (await _t.Erp.Orders.GetOrdersAsync(null, "uninvoiced", 1, 10)).Items.First();
        await _t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice, creditDays);
        return (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Items.First();
    }

    private async Task<InvoiceRow> ReloadAsync(Guid id) => (await _t.Erp.Invoices.GetAsync(id))!;

    // ---------- Notas de crédito ----------

    [Fact]
    public async Task Devolucion_parcial_rebaja_el_saldo_y_no_deja_devolver_dos_veces()
    {
        var invoice = await InvoiceAsync();
        Assert.Equal(193m, invoice.Balance);

        var number = await _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Return, [new NoteLineDraft(1, 2)], "Llegaron dañados");
        Assert.Equal("FTES-000001", number);   // serie de la factura, correlación propia de las notas de crédito

        invoice = await ReloadAsync(invoice.Id);
        Assert.Equal(37m, invoice.Credited);
        Assert.Equal(156m, invoice.Balance);
        Assert.Equal(8m, (await _t.Erp.Invoices.GetLinesAsync(invoice.Id))[0].Returnable);

        var note = Assert.Single(await _t.Erp.Invoices.GetNotesAsync(invoice.Id));
        Assert.Equal(NoteKinds.Credit, note.Kind);
        Assert.Equal(31.36m, note.Subtotal);   // 37.00 / 1.18
        Assert.Equal(5.64m, note.Tax);

        var e = (await _t.OutboxAsync()).Single(x => x.EventType == SuiteEvents.CreditNoteIssued);
        Assert.Equal(note.Id, e.Id);
        Assert.Equal(1, e.Payload!.Value.GetProperty("lines")[0].GetProperty("invoiceLineNo").GetInt32());

        var ex = await Assert.ThrowsAsync<ErpValidationException>(() =>
            _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Return, [new NoteLineDraft(1, 9)]));
        Assert.Contains("solo se puede rebajar 8", ex.Message);
    }

    [Fact]
    public async Task Nota_por_anulacion_rebaja_todo_lo_que_queda()
    {
        var invoice = await InvoiceAsync();
        await _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Discount, [new NoteLineDraft(Amount: 10, Description: "Descuento comercial")]);
        await _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Void, []);

        invoice = await ReloadAsync(invoice.Id);
        Assert.Equal(0m, invoice.Creditable);
        Assert.True(invoice.IsPaid);
        await Assert.ThrowsAsync<ErpValidationException>(() =>
            _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Discount, [new NoteLineDraft(Amount: 1, Description: "Otro")]));
    }

    [Fact]
    public async Task Nota_de_credito_sobre_una_factura_cobrada_deja_saldo_a_favor()
    {
        var invoice = await InvoiceAsync();
        await _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 193m, PaymentMethods.Transfer, "OP-1");
        await _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Return, [new NoteLineDraft(1, 2)]);

        Assert.Equal(-37m, (await ReloadAsync(invoice.Id)).Balance);
        Assert.Empty(await _t.Erp.Invoices.GetReceivablesAsync());
        Assert.Equal(-37m, (await _t.Erp.Invoices.GetStatementAsync(_t.Customer, "PEN", new(2026, 11, 1), new(2026, 11, 30))).ClosingBalance);
    }

    [Fact]
    public async Task Nota_de_debito_suma_al_saldo_y_se_cobra_con_el_comprobante()
    {
        var invoice = await InvoiceAsync();
        await _t.Erp.Invoices.IssueDebitNoteAsync(invoice.Id, NoteReasons.Interest, 15m, "Intereses por mora");
        invoice = await ReloadAsync(invoice.Id);
        Assert.Equal(15m, invoice.Debited);
        Assert.Equal(208m, invoice.Balance);
        Assert.Contains(await _t.OutboxAsync(), e => e.EventType == SuiteEvents.DebitNoteIssued);

        await _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 208m, PaymentMethods.Transfer, null);
        Assert.True((await ReloadAsync(invoice.Id)).IsPaid);
    }

    [Fact]
    public async Task Las_notas_requieren_permiso_y_un_motivo_valido()
    {
        var invoice = await InvoiceAsync();
        await Assert.ThrowsAsync<ErpValidationException>(() =>
            _t.Erp.Invoices.IssueDebitNoteAsync(invoice.Id, NoteReasons.Void, 5, "No es motivo de débito"));
        _t.Context.Role = ErpPermissions.Purchasing;
        await Assert.ThrowsAsync<ErpValidationException>(() =>
            _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Void, []));
    }

    // ---------- Anulación ----------

    [Fact]
    public async Task Anular_un_comprobante_libera_el_pedido_y_publica_invoice_voided()
    {
        var invoice = await InvoiceAsync();
        await _t.Erp.Invoices.VoidAsync(invoice.Id, "RUC equivocado");

        invoice = await ReloadAsync(invoice.Id);
        Assert.True(invoice.IsVoided);
        Assert.Equal(0m, invoice.Balance);
        Assert.Equal(0m, (await _t.Erp.Reports.GetDashboardAsync()).Receivable);
        Assert.Equal(TransactionStatuses.Voided, (await _t.Erp.Reports.GetRecentTransactionsAsync()).Single().Status);

        var e = (await _t.OutboxAsync()).Single(x => x.EventType == SuiteEvents.InvoiceVoided);
        Assert.Equal(DeterministicId.From("invoice_void", invoice.Id), e.Id);

        // El pedido se puede volver a facturar.
        var order = (await _t.Erp.Orders.GetAsync(invoice.OrderId!.Value))!;
        Assert.True(order.CanInvoice);
        Assert.Equal("FTES-000002", await _t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice));
        await Assert.ThrowsAsync<ErpValidationException>(() =>
            _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 10, PaymentMethods.Cash, null));
    }

    [Fact]
    public async Task No_se_anula_con_cobros_ni_sin_permiso()
    {
        var invoice = await InvoiceAsync();
        _t.Context.Role = ErpPermissions.Billing;
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.VoidAsync(invoice.Id, "Error"));
        _t.Context.Role = ErpPermissions.Accountant;
        await _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 10, PaymentMethods.Cash, null);
        var ex = await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.VoidAsync(invoice.Id, "Error"));
        Assert.Contains("nota de crédito", ex.Message);
    }

    // ---------- Otros equipos del ERP ----------

    [Fact]
    public async Task Notas_y_anulaciones_de_otro_equipo_se_aplican_una_vez()
    {
        var invoice = await InvoiceAsync();
        await _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Return, [new NoteLineDraft(1, 1)]);
        var outbox = await _t.OutboxAsync();

        var other = new TestWorkspace("OTRO");
        await other.InitializeAsync();
        try
        {
            await other.Erp.SyncHandlers.HandleInvoiceIssuedAsync(TestWorkspace.AsEvent(outbox.Single(e => e.EventType == SuiteEvents.InvoiceIssued)));
            var note = TestWorkspace.AsEvent(outbox.Single(e => e.EventType == SuiteEvents.CreditNoteIssued));
            await other.Erp.SyncHandlers.HandleCreditNoteIssuedAsync(note);
            await other.Erp.SyncHandlers.HandleCreditNoteIssuedAsync(note);

            var replica = (await other.Erp.Invoices.GetAsync(invoice.Id))!;
            Assert.Equal(18.50m, replica.Credited);
            Assert.Equal(9m, (await other.Erp.Invoices.GetLinesAsync(invoice.Id))[0].Returnable);
            Assert.Equal("remote", Assert.Single(await other.Erp.Invoices.GetNotesAsync()).Origin);

            // Una nota que no viene del ERP se ignora.
            await other.Erp.SyncHandlers.HandleCreditNoteIssuedAsync(TestWorkspace.Event(SuiteEvents.CreditNoteIssued, new
            {
                noteId = Guid.NewGuid(), number = "X", invoiceId = invoice.Id, reason = "void", total = 100m, currencyCode = "PEN",
            }, "CRM"));
            Assert.Equal(18.50m, (await other.Erp.Invoices.GetAsync(invoice.Id))!.Credited);
        }
        finally
        {
            await other.DisposeAsync();
        }
    }

    // ---------- Antigüedad de saldos y estado de cuenta ----------

    [Fact]
    public async Task La_antiguedad_reparte_el_saldo_por_dias_de_vencido()
    {
        var old = await InvoiceAsync(creditDays: 30);                  // vence el 2 de diciembre
        _t.Clock.Advance(TimeSpan.FromDays(45));                       // 17 de diciembre: 15 días vencida
        await InvoiceAsync(creditDays: 30);                             // vigente
        await _t.Erp.Invoices.RegisterPaymentAsync(old.Id, 93m, PaymentMethods.Transfer, null);

        var row = Assert.Single(await _t.Erp.Invoices.GetAgingAsync());
        Assert.Equal("PEN", row.CurrencyCode);
        Assert.Equal(193m, row.Current);
        Assert.Equal(100m, row.Days1To30);
        Assert.Equal(0m, row.Over90);
        Assert.Equal(100m, row.Overdue);
        Assert.Equal(293m, row.Total);
    }

    [Fact]
    public async Task El_estado_de_cuenta_lleva_saldo_anterior_movimientos_y_pdf()
    {
        var invoice = await InvoiceAsync();                                        // 2 de noviembre: +193
        _t.Clock.Advance(TimeSpan.FromDays(3));
        await _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 50m, PaymentMethods.Transfer, "OP-7");   // 5 de noviembre: −50
        _t.Clock.Advance(TimeSpan.FromDays(5));
        await _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Discount, [new NoteLineDraft(Amount: 10, Description: "Bonificación")]);

        var month = await _t.Erp.Invoices.GetStatementAsync(_t.Customer, "PEN", new(2026, 11, 1), new(2026, 11, 30));
        Assert.Equal(0m, month.OpeningBalance);
        Assert.Equal([StatementEntryKinds.Invoice, StatementEntryKinds.Payment, StatementEntryKinds.CreditNote], month.Entries.Select(e => e.Kind));
        Assert.Equal([193m, 143m, 133m], month.Entries.Select(e => e.Balance));
        Assert.Equal(133m, month.ClosingBalance);

        var tail = await _t.Erp.Invoices.GetStatementAsync(_t.Customer, "PEN", new(2026, 11, 6), new(2026, 11, 30));
        Assert.Equal(143m, tail.OpeningBalance);
        Assert.Single(tail.Entries);

        var pdf = StatementPdf.Generate(month, await _t.Erp.Master.GetCompanyAsync(), System.Globalization.CultureInfo.GetCultureInfo("es-PE"));
        Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);
    }

    // ---------- Ingresos y sedes ----------

    [Fact]
    public async Task Los_ingresos_del_mes_restan_las_notas_de_credito_y_no_cuentan_lo_anulado()
    {
        var invoice = await InvoiceAsync();
        await _t.Erp.Invoices.IssueCreditNoteAsync(invoice.Id, NoteReasons.Return, [new NoteLineDraft(1, 2)]);
        Assert.Equal(156m, (await _t.Erp.Reports.GetDashboardAsync()).RevenueMonth);

        var second = await InvoiceAsync();
        await _t.Erp.Invoices.VoidAsync(second.Id, "Duplicado");
        Assert.Equal(156m, (await _t.Erp.Reports.GetDashboardAsync()).RevenueMonth);
    }

    [Fact]
    public async Task Un_usuario_solo_usa_sus_sedes()
    {
        await _t.Erp.Orders.CreateAsync(_t.Customer, _t.Central, [new DocumentLine(_t.Coffee, 1)], "PEN");
        _t.Context.SiteIds = [_t.Store];

        var ex = await Assert.ThrowsAsync<ErpValidationException>(() =>
            _t.Erp.Orders.CreateAsync(_t.Customer, _t.Central, [new DocumentLine(_t.Coffee, 1)], "PEN"));
        Assert.Contains("No tienes asignada", ex.Message);
        await Assert.ThrowsAsync<ErpValidationException>(() =>
            _t.Erp.Purchases.CreateAsync(_t.Supplier, _t.Central, [new DocumentLine(_t.Coffee, 1, 10m)], "PEN"));
        Assert.Empty((await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items);

        await _t.Erp.Orders.CreateAsync(_t.Customer, _t.Store, [new DocumentLine(_t.Coffee, 1)], "PEN");
        Assert.Single((await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items);
    }
}
