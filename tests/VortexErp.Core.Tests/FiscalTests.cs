using VortexApi.Client;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Services;

namespace VortexErp.Core.Tests;

/// <summary>Fase 7a: el ERP emite comprobantes electrónicos de Perú a través del hub.</summary>
public sealed class FiscalTests : IAsyncLifetime
{
    private const string CompanyRuc = "20123456786";
    private const string CustomerRuc = "20111222330";
    private readonly TestWorkspace _t = new("EQPA");
    private readonly FakeGateway _hub = new();
    private readonly Guid _company = Guid.Parse("33333333-0000-0000-0000-000000000010");

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _t.Erp.Master.ApplyCompanyAsync(new VortexMasterChange<VortexCompany>(2, Guid.Empty, VortexChangeOperation.Updated, 2, DateTimeOffset.UtcNow, "HUB",
            new VortexCompany(Guid.NewGuid(), "Comercial Demo SAC", null, CompanyRuc, "PE", "PEN", "America/Lima", 18, null, null, null,
                new VortexMasterMeta(2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null))));
        await _t.Erp.Master.ApplyPartyAsync(TestWorkspace.Party(_company, "Restaurante El Sol SAC", "RUC", CustomerRuc));
        _t.Context.Features = [FiscalService.Feature];
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<(Guid Id, string Number)> InvoiceAsync(Guid customer, string kind, decimal coffee = 2)
    {
        var order = await _t.Erp.Orders.CreateAsync(customer, null, [new DocumentLine(_t.Coffee, coffee)], "PEN");
        var orderId = (await _t.Erp.Orders.GetOrdersAsync(order, null, 1, 1)).Items.Single().Id;
        var number = await _t.Erp.Invoices.IssueFromOrderAsync(orderId, kind);
        return ((await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery(number, null, null, null, 1, 1))).Items.Single().Id, number);
    }

    [Fact]
    public async Task La_factura_al_credito_y_su_nota_de_anulacion_salen_al_hub_con_la_serie_del_equipo()
    {
        Assert.True((await _t.Erp.Fiscal.GetSetupAsync()).Enabled);
        var (id, number) = await InvoiceAsync(_company, InvoiceKinds.Invoice);
        Assert.Equal("FEQP-000001", number);

        Assert.Equal(1, await _t.Erp.Fiscal.SendPendingAsync(_hub));
        var request = Assert.Single(_hub.Issued);
        Assert.Equal((id, VortexFiscalTypes.Invoice, "FEQP", 1L), (request.SourceId, request.Type, request.Series, request.Number));
        Assert.Equal(("6", CustomerRuc), (request.Customer.DocType, request.Customer.DocNumber));
        Assert.Equal(37m, request.Lines.Sum(l => l.Total));                 // 2 cafés de S/ 18.50 con IGV
        Assert.Equal((VortexFiscalTaxCategories.Taxed, 0.18m, "CAFE-250"), (request.Lines[0].TaxCategory, request.Lines[0].TaxRate, request.Lines[0].ItemCode));
        Assert.True(request.Payment!.Credit);                               // al crédito: una cuota al vencimiento
        Assert.Equal(37m, request.Payment.Installments!.Single().Amount);

        // Un comprobante electrónico no se anula: se emite la nota de crédito por anulación (motivo 01).
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.VoidAsync(id, "Error de precio"));
        await _t.Erp.Invoices.IssueCreditNoteAsync(id, NoteReasons.Void, []);
        await _t.Erp.Fiscal.SendPendingAsync(_hub);
        var note = _hub.Issued[1];
        Assert.Equal((VortexFiscalTypes.CreditNote, "FEQP"), (note.Type, note.Series));
        Assert.Equal(("invoice", "FEQP", 1L, "01"), (note.Reference!.Type, note.Reference.Series, note.Reference.Number, note.Reference.ReasonCode));

        // La respuesta de SUNAT llega a todos los equipos.
        await _t.Erp.SyncHandlers.HandleFiscalResponseAsync(TestWorkspace.Event(SuiteEvents.FiscalAccepted,
            new VortexFiscalResponded(id, "ERP", VortexFiscalTypes.Invoice, "FEQP", 1, VortexFiscalStatus.Accepted, "abc=", "20123456786|01|FEQP|1|", "0",
                "La Factura numero FEQP-1, ha sido aceptada", null), source: "HUB"));
        var docs = await _t.Erp.Fiscal.GetForInvoiceAsync(id);
        Assert.Equal(2, docs.Count);
        var accepted = docs.Single(d => d.Id == id);
        Assert.Equal((FiscalStatuses.Accepted, "abc=", "FEQP-000001"), (accepted.Status, accepted.Hash, accepted.FullNumber));

        var pdf = await _t.Erp.Fiscal.InvoicePdfAsync(id, System.Globalization.CultureInfo.GetCultureInfo("es-PE"));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal("20123456786|01|FEQP|1|5.64|37.00|2026-11-02|6|20111222330|abc=|",
            FiscalService.QrText(CompanyRuc, VortexFiscalTypes.Invoice, "FEQP", 1, 5.64m, 37m, new DateOnly(2026, 11, 2), CustomerRuc, "abc="));
    }

    [Fact]
    public async Task Las_reglas_de_sunat_se_validan_antes_de_numerar()
    {
        // El cliente de prueba de siempre tiene un RUC con dígito verificador inválido: no recibe factura.
        await Assert.ThrowsAsync<ErpValidationException>(() => InvoiceAsync(_t.Customer, InvoiceKinds.Invoice));
        // Boleta a una persona con DNI: va con su documento (catálogo 6: 1).
        await InvoiceAsync(_t.Person, InvoiceKinds.Receipt);
        await _t.Erp.Fiscal.SendPendingAsync(_hub);
        Assert.Equal(("receipt", "BEQP", "1"), (_hub.Issued.Single().Type, _hub.Issued.Single().Series, _hub.Issued.Single().Customer.DocType));
    }

    [Fact]
    public async Task Sin_red_queda_en_cola_un_numero_repetido_queda_en_error_y_sin_el_modulo_factura_como_antes()
    {
        var (id, _) = await InvoiceAsync(_company, InvoiceKinds.Invoice);
        _hub.Offline = true;
        Assert.Equal(0, await _t.Erp.Fiscal.SendPendingAsync(_hub));
        Assert.Equal(FiscalStatuses.Pending, Assert.Single(await _t.Erp.Fiscal.GetOutstandingAsync()).Status);

        _hub.Offline = false;
        _hub.Reject = new VortexApiException(409, "fiscal_number_in_use", "El número FEQP-1 ya se usó");
        await _t.Erp.Fiscal.SendPendingAsync(_hub);
        var error = Assert.Single(await _t.Erp.Fiscal.GetForInvoiceAsync(id));
        Assert.Equal(FiscalStatuses.Error, error.Status);
        _hub.Reject = null;
        await _t.Erp.Fiscal.RetryAsync(_hub, id);
        Assert.Single(_hub.Issued);

        _t.Context.Features = [];
        Assert.False((await _t.Erp.Fiscal.GetSetupAsync()).Enabled);
        var (plain, _) = await InvoiceAsync(_company, InvoiceKinds.Invoice);
        Assert.Empty(await _t.Erp.Fiscal.GetForInvoiceAsync(plain));
        await _t.Erp.Invoices.VoidAsync(plain, "Emitida por error");   // uno que no es electrónico sí se anula
    }

    private sealed class FakeGateway : IFiscalGateway
    {
        public List<VortexFiscalRequest> Issued { get; } = [];
        public bool Offline { get; set; }
        public VortexApiException? Reject { get; set; }

        public Task<VortexFiscalDocument> IssueAsync(VortexFiscalRequest request, CancellationToken ct = default)
        {
            if (Offline)
                throw new HttpRequestException("Sin conexión");
            if (Reject is { } ex)
                throw ex;
            Issued.Add(request);
            return Task.FromResult(new VortexFiscalDocument(Guid.NewGuid(), request.SourceId, "ERP", request.Type, request.Series, request.Number,
                request.IssueDate, request.Currency, request.Lines.Sum(l => l.Total ?? 0), VortexFiscalStatus.Pending, 0, null, null, null, null, null,
                null, DateTimeOffset.UtcNow, null));
        }

        public Task<VortexFiscalDocument> RetryAsync(Guid hubId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
