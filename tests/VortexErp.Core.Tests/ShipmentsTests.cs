using VortexApi.Client.Sync;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;

namespace VortexErp.Core.Tests;

/// <summary>Fase 3b de la hoja de ruta: facturación de despachos parciales y recordatorios de cobranza.</summary>
public sealed class ShipmentsTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<OrderRow> OrderAsync(string number) => (await _t.Erp.Orders.GetOrdersAsync(number, null, 1, 5)).Items.Single();

    private Task DispatchAsync(Guid orderId, string number, bool? partial, params QuantityLine[] lines) =>
        _t.Erp.SyncHandlers.HandleShipmentDispatchedAsync(TestWorkspace.Event(SuiteEvents.ShipmentDispatched,
            new ShipmentDispatched(Guid.NewGuid(), orderId, _t.Customer, "Shalom", null, number, lines.Length == 0 ? null : lines, partial),
            source: "WMS"));

    [Fact]
    public async Task Un_pedido_despachado_en_dos_partes_se_factura_en_dos_comprobantes()
    {
        // 10 cafés a 18.50 y 20 panes a 0.40.
        var number = await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 10), new DocumentLine(_t.Bread, 20, 0.40m)], "PEN");
        var order = await OrderAsync(number);

        await DispatchAsync(order.Id, "DES-1", true, new QuantityLine(_t.Coffee, 6), new QuantityLine(_t.Bread, 20));
        order = await OrderAsync(number);
        Assert.Equal((OrderStatuses.Partial, 1, true), (order.Status, order.PendingShipments, order.CanInvoiceShipments));

        var shipments = await _t.Erp.Orders.GetShipmentsAsync(order.Id);
        var first = await _t.Erp.Invoices.IssueFromShipmentAsync(shipments[0].Id, InvoiceKinds.Invoice);
        var invoice = (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery(Search: first))).Items.Single();
        Assert.Equal(6 * 18.50m + 8.00m, invoice.Total);
        order = await OrderAsync(number);
        Assert.False(order.CanInvoice);   // ya se facturó una parte: el pedido entero no

        // El resto: sin líneas en el evento, sale todo lo que faltaba (4 cafés).
        await DispatchAsync(order.Id, "DES-2", null);
        order = await OrderAsync(number);
        Assert.Equal(OrderStatuses.Dispatched, order.Status);
        shipments = await _t.Erp.Orders.GetShipmentsAsync(order.Id);
        Assert.Equal(4, shipments[1].Lines.Single().Quantity);
        var second = await _t.Erp.Invoices.IssueFromShipmentAsync(shipments[1].Id, InvoiceKinds.Invoice);
        Assert.Equal(74.00m, (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery(Search: second))).Items.Single().Total);
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.IssueFromShipmentAsync(shipments[1].Id, InvoiceKinds.Invoice));
        Assert.Empty((await _t.Erp.Orders.GetOrdersAsync(null, "uninvoiced", 1, 10)).Items.Where(o => o.Id == order.Id));

        // El comprobante lleva el despacho: otro equipo del ERP lo enlaza con esa parte.
        var issued = (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.InvoiceIssued).ToList();
        Assert.All(issued, e => Assert.True(e.Payload!.Value.TryGetProperty("shipmentId", out _)));
    }

    [Fact]
    public async Task Repetir_el_despacho_no_lo_duplica_y_anular_la_factura_libera_esa_parte()
    {
        var number = await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 4)], "PEN");
        var order = await OrderAsync(number);
        var e = TestWorkspace.Event(SuiteEvents.ShipmentDispatched,
            new ShipmentDispatched(Guid.NewGuid(), order.Id, _t.Customer, null, null, "DES-9", [new QuantityLine(_t.Coffee, 4)]), source: "WMS");
        await _t.Erp.SyncHandlers.HandleShipmentDispatchedAsync(e);
        await _t.Erp.SyncHandlers.HandleShipmentDispatchedAsync(e);
        var shipment = Assert.Single(await _t.Erp.Orders.GetShipmentsAsync(order.Id));

        var invoiceNumber = await _t.Erp.Invoices.IssueFromShipmentAsync(shipment.Id, InvoiceKinds.Invoice);
        var invoice = (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery(Search: invoiceNumber))).Items.Single();
        await _t.Erp.Invoices.VoidAsync(invoice.Id, "Error en el RUC");
        Assert.Null((await _t.Erp.Orders.GetShipmentsAsync(order.Id)).Single().InvoiceId);
        Assert.True((await OrderAsync(number)).CanInvoice);   // se puede volver a facturar entero o por parte
    }

    [Fact]
    public async Task El_recordatorio_de_cobranza_lista_lo_vencido_y_queda_registrado()
    {
        var number = await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 2)], "PEN");
        await _t.Erp.Invoices.IssueFromOrderAsync((await OrderAsync(number)).Id, InvoiceKinds.Invoice, creditDays: 0);
        Assert.Null(await _t.Erp.Invoices.GetReminderAsync(_t.Customer, "PEN"));   // vence hoy: aún no está vencido

        _t.Clock.Advance(TimeSpan.FromDays(10));
        var reminder = (await _t.Erp.Invoices.GetReminderAsync(_t.Customer, "PEN"))!;
        Assert.Equal(37.00m, reminder.Overdue);
        Assert.Contains("Bodega Don Pepe", reminder.Message);
        Assert.Contains("hace 10 días", reminder.Message);
        Assert.Null(reminder.LastSentAt);

        await _t.Erp.Invoices.RecordReminderAsync(_t.Customer, ReminderChannels.WhatsApp, reminder.Overdue);
        Assert.NotNull((await _t.Erp.Invoices.GetReminderAsync(_t.Customer, "PEN"))!.LastSentAt);
        Assert.True((await _t.Erp.Invoices.GetLastRemindersAsync()).ContainsKey(_t.Customer));
    }
}
