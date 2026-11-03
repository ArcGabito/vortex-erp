using VortexErp.Core.Domain;
using VortexErp.Core.Integration;

namespace VortexErp.Core.Tests;

public sealed class SyncTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private object Sale(Guid saleId, decimal cash, decimal credit = 0) => new
    {
        saleId, number = "V-9AFE-000001", siteId = _t.Store, customerId = credit > 0 ? _t.Customer : (Guid?)null, currencyCode = "PEN",
        subtotal = 31.36m + 1m, tax = 5.64m, total = 38m,
        lines = new object[]
        {
            new { itemId = _t.Coffee, sku = "CAFE-250", name = "Café tostado 250 g", quantity = 2m, unitPrice = 18.50m, discount = 0m, total = 37m, taxable = true },
            new { itemId = _t.Bread, sku = "PAN-01", quantity = 2m, unitPrice = 0.50m, discount = 0m, total = 1m, taxable = false },
        },
        payments = credit > 0 ? new[] { new { method = "cash", amount = cash }, new { method = "credit", amount = credit } }
                              : new[] { new { method = "cash", amount = cash } },
    };

    [Fact]
    public async Task Venta_de_caja_cuenta_como_ingreso_y_su_boleta_queda_pagada()
    {
        var saleId = Guid.NewGuid();
        var sale = TestWorkspace.Event(SuiteEvents.SaleCompleted, Sale(saleId, 38m), "POS");
        await _t.Erp.SyncHandlers.HandleSaleCompletedAsync(sale);
        await _t.Erp.SyncHandlers.HandleSaleCompletedAsync(sale);

        Assert.Equal(38m, (await _t.Erp.Reports.GetDashboardAsync()).RevenueMonth);
        var pending = Assert.Single((await _t.Erp.Invoices.GetPosSalesAsync(uninvoicedOnly: true, 1, 10)).Items);

        var number = await _t.Erp.Invoices.IssueFromSaleAsync(pending.Id, InvoiceKinds.Receipt);
        var invoice = (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery(Search: number))).Items.Single();
        Assert.True(invoice.IsPaid);
        Assert.Equal(5.64m, invoice.Tax);
        Assert.Equal(saleId, invoice.SaleId);
        Assert.Empty((await _t.Erp.Invoices.GetPosSalesAsync(uninvoicedOnly: true, 1, 10)).Items);
        // El comprobante de una venta de caja no se cuenta dos veces como ingreso.
        Assert.Equal(38m, (await _t.Erp.Reports.GetDashboardAsync()).RevenueMonth);

        var e = (await _t.OutboxAsync()).Single(x => x.EventType == SuiteEvents.InvoiceIssued).Payload!.Value;
        Assert.Equal(saleId, e.GetProperty("saleId").GetGuid());
    }

    [Fact]
    public async Task Venta_al_credito_deja_por_cobrar_solo_la_parte_al_credito()
    {
        var saleId = Guid.NewGuid();
        await _t.Erp.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, Sale(saleId, 8m, credit: 30m), "POS"));
        await _t.Erp.Invoices.IssueFromSaleAsync(saleId, InvoiceKinds.Invoice);

        var receivable = Assert.Single(await _t.Erp.Invoices.GetReceivablesAsync());
        Assert.Equal(_t.Customer, receivable.CustomerId);
        Assert.Equal(30m, receivable.Balance);
    }

    [Fact]
    public async Task Anulacion_y_devolucion_del_POS_descuentan_el_ingreso()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await _t.Erp.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, Sale(a, 38m), "POS"));
        await _t.Erp.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, Sale(b, 38m), "POS"));
        await _t.Erp.SyncHandlers.HandleSaleVoidedAsync(TestWorkspace.Event(SuiteEvents.SaleVoided, new { saleId = a, reason = "Duplicada" }, "POS"));
        await _t.Erp.SyncHandlers.HandleSaleReturnedAsync(TestWorkspace.Event(SuiteEvents.SaleReturned,
            new { saleId = b, returnId = Guid.NewGuid(), siteId = _t.Store, total = 18.50m }, "POS"));

        Assert.Equal(19.50m, (await _t.Erp.Reports.GetDashboardAsync()).RevenueMonth);
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.IssueFromSaleAsync(a, InvoiceKinds.Receipt));
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.IssueFromSaleAsync(b, InvoiceKinds.Receipt));
    }

    [Fact]
    public async Task Orden_de_compra_se_completa_con_las_recepciones_del_WMS_y_fija_el_costo_del_inventario()
    {
        await _t.Erp.Purchases.CreateAsync(_t.Supplier, null, [new DocumentLine(_t.Coffee, 10, 11m)], "PEN", new DateOnly(2026, 11, 10));
        var po = (await _t.Erp.Purchases.GetPurchasesAsync(null, null, 1, 10)).Items.Single();
        var e = Assert.Single(await _t.OutboxAsync());
        Assert.Equal(SuiteEvents.PurchaseOrderCreated, e.EventType);
        Assert.Equal(_t.Central, e.Payload!.Value.GetProperty("siteId").GetGuid());

        await _t.Erp.SyncHandlers.HandleGoodsReceivedAsync(TestWorkspace.Event(SuiteEvents.GoodsReceived,
            new { receiptId = Guid.NewGuid(), purchaseOrderId = po.Id, siteId = _t.Central, lines = new[] { new { itemId = _t.Coffee, quantity = 4m } } }, "WMS"));
        Assert.Equal(PurchaseStatuses.Partial, (await _t.Erp.Purchases.GetPurchasesAsync(null, null, 1, 10)).Items.Single().Status);
        await _t.Erp.SyncHandlers.HandleGoodsReceivedAsync(TestWorkspace.Event(SuiteEvents.GoodsReceived,
            new { receiptId = Guid.NewGuid(), purchaseOrderId = po.Id, siteId = _t.Central, lines = new[] { new { itemId = _t.Coffee, quantity = 6m } } }, "WMS"));
        Assert.Equal(PurchaseStatuses.Received, (await _t.Erp.Purchases.GetPurchasesAsync(null, null, 1, 10)).Items.Single().Status);

        await _t.Erp.SyncHandlers.HandleStockAdjustedAsync(TestWorkspace.Event(SuiteEvents.StockAdjusted,
            new { siteId = _t.Central, itemId = _t.Coffee, quantityDelta = 10m, quantityOnHand = 10m, reason = "receipt" }, "WMS"));
        await _t.Erp.SyncHandlers.HandleStockAdjustedAsync(TestWorkspace.Event(SuiteEvents.StockAdjusted,
            new { siteId = _t.Store, itemId = _t.Bread, quantityDelta = 5m, quantityOnHand = 5m, reason = "receipt" }, "WMS"));

        var stock = (await _t.Erp.Reports.GetStockAsync(null, null, 1, 10)).Items;
        var coffee = stock.Single(s => s.ItemId == _t.Coffee);
        Assert.True(coffee.CostFromPurchase);
        Assert.Equal(110m, coffee.Value);                                       // 10 × costo de compra 11.00
        Assert.Equal(2.50m, stock.Single(s => s.ItemId == _t.Bread).Value);     // 5 × precio de lista (sin compras)
        Assert.Equal(112.50m, await _t.Erp.Reports.GetInventoryValueAsync());
    }

    [Fact]
    public async Task Comprobante_y_cobro_de_otro_equipo_del_ERP_se_replican_una_vez()
    {
        var other = new TestWorkspace("ERP2");
        await other.InitializeAsync();
        try
        {
            await other.Erp.Orders.CreateAsync(other.Customer, null, [new DocumentLine(other.Coffee, 2)], "PEN");
            var order = (await other.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.Single();
            await other.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice);
            var invoice = (await other.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Items.Single();
            await other.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 10, PaymentMethods.Cash, null);

            foreach (var entry in await other.OutboxAsync())
            {
                var e = TestWorkspace.AsEvent(entry, "ERP");
                await (entry.EventType switch
                {
                    SuiteEvents.OrderCreated => _t.Erp.SyncHandlers.HandleOrderCreatedAsync(e),
                    SuiteEvents.InvoiceIssued => _t.Erp.SyncHandlers.HandleInvoiceIssuedAsync(e),
                    SuiteEvents.PaymentReceived => _t.Erp.SyncHandlers.HandlePaymentReceivedAsync(e),
                    _ => Task.CompletedTask,
                });
                await (entry.EventType == SuiteEvents.PaymentReceived ? _t.Erp.SyncHandlers.HandlePaymentReceivedAsync(e) : Task.CompletedTask);
            }

            var replica = (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Items.Single();
            Assert.Equal("remote", replica.Origin);
            Assert.Equal("FERP-000001", replica.Number);
            Assert.Equal(27m, replica.Balance);
            Assert.Equal(replica.Number, (await _t.Erp.Orders.GetAsync(order.Id))!.InvoiceNumber);
            Assert.Single((await _t.Erp.Invoices.GetPaymentsAsync(null, 1, 10)).Items);
        }
        finally
        {
            await other.DisposeAsync();
        }
    }

    [Fact]
    public async Task Eventos_de_otra_version_o_de_otro_sistema_se_ignoran_sin_trabar_el_feed()
    {
        await _t.Erp.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new { saleId = Guid.NewGuid() }, "POS", schemaVersion: 2));
        await _t.Erp.SyncHandlers.HandleInvoiceIssuedAsync(TestWorkspace.Event(SuiteEvents.InvoiceIssued,
            new { invoiceId = Guid.NewGuid(), number = "X-1", total = 1m, currencyCode = "PEN" }, "CRM"));
        await _t.Erp.SyncHandlers.HandleGoodsReceivedAsync(TestWorkspace.Event(SuiteEvents.GoodsReceived,
            new { receiptId = Guid.NewGuid(), purchaseOrderId = Guid.NewGuid(), siteId = _t.Central }, "WMS"));

        Assert.Equal(0, (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Total);
        Assert.Equal(0, (await _t.Erp.Invoices.GetPosSalesAsync(false, 1, 10)).Total);
    }
}
