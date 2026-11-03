using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Services;

namespace VortexErp.Core.Tests;

public sealed class SalesTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Pedido_publica_order_created_con_el_precio_de_lista_y_la_sede_por_defecto()
    {
        var number = await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 10), new DocumentLine(_t.Bread, 20, 0.40m)], "PEN");
        Assert.Equal("PED-TEST-000001", number);

        var order = (await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.Single();
        Assert.Equal(193m, order.Total);   // 10 × 18.50 + 20 × 0.40
        Assert.Equal(_t.Central, order.SiteId);
        Assert.Equal("ERP", order.Source);

        var e = Assert.Single(await _t.OutboxAsync());
        Assert.Equal(SuiteEvents.OrderCreated, e.EventType);
        Assert.Equal(order.Id, e.Id);
        Assert.Equal(_t.Customer, e.Payload!.Value.GetProperty("customerId").GetGuid());
        Assert.Equal(0.40m, e.Payload!.Value.GetProperty("lines")[1].GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task Cotizacion_aceptada_en_el_CRM_se_convierte_en_pedido_con_id_determinista()
    {
        var quoteId = Guid.NewGuid();
        await _t.Erp.SyncHandlers.HandleQuoteAcceptedAsync(TestWorkspace.Event(SuiteEvents.QuoteAccepted, new
        {
            quoteId, number = "COT-0007", customerId = _t.Customer, currencyCode = "PEN", total = 37m,
            lines = new[] { new { itemId = _t.Coffee, quantity = 2m, unitPrice = 18.50m } },
        }, "CRM"));
        Assert.Single(await _t.Erp.Orders.GetQuotesAsync(pendingOnly: true));

        await _t.Erp.Orders.ConvertQuoteAsync(quoteId, null);
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Orders.ConvertQuoteAsync(quoteId, null));

        Assert.Empty(await _t.Erp.Orders.GetQuotesAsync(pendingOnly: true));
        var order = (await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.Single();
        Assert.Equal(OrderService.OrderIdForQuote(quoteId), order.Id);
        Assert.Equal(quoteId, order.QuoteId);
        Assert.Equal(37m, order.Total);
    }

    [Fact]
    public async Task Si_el_CRM_ya_genero_el_pedido_la_cotizacion_queda_vinculada_sin_duplicarlo()
    {
        var quoteId = Guid.NewGuid();
        var orderId = OrderService.OrderIdForQuote(quoteId);
        await _t.Erp.SyncHandlers.HandleOrderCreatedAsync(TestWorkspace.Event(SuiteEvents.OrderCreated, new
        {
            orderId, number = "PED-CRM-0001", customerId = _t.Customer, quoteId, currencyCode = "PEN", total = 18.50m,
            lines = new[] { new { itemId = _t.Coffee, quantity = 1m, unitPrice = 18.50m } },
        }, "CRM"));
        await _t.Erp.SyncHandlers.HandleQuoteAcceptedAsync(TestWorkspace.Event(SuiteEvents.QuoteAccepted, new
        {
            quoteId, customerId = _t.Customer, total = 18.50m, lines = new[] { new { itemId = _t.Coffee, quantity = 1m, unitPrice = 18.50m } },
        }, "CRM"));

        Assert.Equal("PED-CRM-0001", Assert.Single(await _t.Erp.Orders.GetQuotesAsync(pendingOnly: false)).OrderNumber);
        Assert.Equal("CRM", (await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.Single().Source);
        Assert.Empty(await _t.OutboxAsync());
    }

    [Fact]
    public async Task Factura_exige_RUC_desglosa_el_igv_y_publica_invoice_issued()
    {
        await _t.Erp.Orders.CreateAsync(_t.Person, null, [new DocumentLine(_t.Coffee, 1)], "PEN");
        await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 2), new DocumentLine(_t.Bread, 10)], "PEN");
        var orders = (await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items;
        var toPerson = orders.Single(o => o.CustomerId == _t.Person);
        var toCompany = orders.Single(o => o.CustomerId == _t.Customer);

        var ex = await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.IssueFromOrderAsync(toPerson.Id, InvoiceKinds.Invoice));
        Assert.Contains("RUC", ex.Message);
        Assert.Equal("BTES-000001", await _t.Erp.Invoices.IssueFromOrderAsync(toPerson.Id, InvoiceKinds.Receipt));

        var number = await _t.Erp.Invoices.IssueFromOrderAsync(toCompany.Id, InvoiceKinds.Invoice);
        Assert.Equal("FTES-000001", number);
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.IssueFromOrderAsync(toCompany.Id, InvoiceKinds.Invoice));

        var invoice = (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery(Search: number))).Items.Single();
        Assert.Equal(31.36m + 5m, invoice.Subtotal);   // 37.00 / 1.18 + pan exonerado
        Assert.Equal(5.64m, invoice.Tax);
        Assert.Equal(42m, invoice.Total);
        Assert.Equal(new DateOnly(2026, 12, 2), invoice.DueDate);
        Assert.Equal(number, (await _t.Erp.Orders.GetAsync(toCompany.Id))!.InvoiceNumber);

        var e = (await _t.OutboxAsync()).Single(x => x.EventType == SuiteEvents.InvoiceIssued && x.Id == invoice.Id).Payload!.Value;
        Assert.Equal(toCompany.Id, e.GetProperty("orderId").GetGuid());
        Assert.Equal("factura", e.GetProperty("kind").GetString());
        Assert.Equal(2, e.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task Cobros_parciales_reducen_el_saldo_y_el_vencido_aparece_en_cuentas_por_cobrar()
    {
        await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 4)], "PEN");
        var order = (await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.Single();
        await _t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice, creditDays: 15);
        var invoice = (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Items.Single();

        Assert.Equal("COB-TEST-000001", await _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 50, PaymentMethods.Transfer, "OP 123"));
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 30, PaymentMethods.Cash, null));

        _t.Clock.Advance(TimeSpan.FromDays(20));
        var receivable = Assert.Single(await _t.Erp.Invoices.GetReceivablesAsync());
        Assert.Equal(24m, receivable.Balance);
        Assert.Equal(24m, receivable.Overdue);
        var summary = await _t.Erp.Reports.GetDashboardAsync();
        Assert.Equal(1, summary.OverdueInvoices);

        await _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 24, PaymentMethods.Cash, null);
        Assert.True((await _t.Erp.Invoices.GetAsync(invoice.Id))!.IsPaid);
        Assert.Empty(await _t.Erp.Invoices.GetReceivablesAsync());

        var paid = (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.PaymentReceived).ToList();
        Assert.Equal([50m, 24m], paid.Select(p => p.Payload!.Value.GetProperty("amount").GetDecimal()));
        Assert.All(paid, p => Assert.Equal(invoice.Id, p.Payload!.Value.GetProperty("invoiceId").GetGuid()));
    }

    [Fact]
    public async Task Pedido_despachado_por_el_WMS_queda_despachado()
    {
        await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 1)], "PEN");
        var order = (await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.Single();
        await _t.Erp.SyncHandlers.HandleShipmentDispatchedAsync(TestWorkspace.Event(SuiteEvents.ShipmentDispatched, new
        {
            shipmentId = Guid.NewGuid(), orderId = order.Id, customerId = _t.Customer, carrier = "Olva", trackingNumber = "OLV-1", number = "DES-9AFE-00001",
        }, "WMS"));

        var dispatched = (await _t.Erp.Orders.GetAsync(order.Id))!;
        Assert.Equal(OrderStatuses.Dispatched, dispatched.Status);
        Assert.Equal("Olva", dispatched.Carrier);
        Assert.False(dispatched.CanCancel);
    }
}
