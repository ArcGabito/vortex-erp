using VortexApi.Client.Sync;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;

namespace VortexErp.Core.Tests;

/// <summary>Fase 4a: solicitudes con aprobación por monto, conciliación de tres vías, cuentas por pagar y pagos.</summary>
public sealed class PayablesTests : IAsyncLifetime
{
    // Café S/ 18.50 (IGV) y pan (exonerado). Supplier es un proveedor con RUC. Fecha: 2 de noviembre de 2026.
    private readonly TestWorkspace _a = new("AAAA");
    private readonly TestWorkspace _b = new("BBBB");

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _b.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private async Task<(Guid Id, string Number)> OrderAsync(decimal qty, decimal cost)
    {
        var number = await _a.Erp.Purchases.CreateAsync(_a.Supplier, _a.Central, [new DocumentLine(_a.Coffee, qty, cost)], "PEN");
        return ((await _a.Erp.Purchases.GetPurchasesAsync(number, null, 1, 1)).Items.Single().Id, number);
    }

    private Task ReceiveAsync(Guid po, decimal qty) => _a.Erp.SyncHandlers.HandleGoodsReceivedAsync(TestWorkspace.Event(SuiteEvents.GoodsReceived,
        new GoodsReceived(Guid.NewGuid(), "REC-1", po, _a.Central, [new QuantityLine(_a.Coffee, qty)]), source: "WMS"));

    [Fact]
    public async Task Una_compra_sobre_el_umbral_se_pide_se_aprueba_y_se_convierte_en_orden()
    {
        await _a.Erp.Payables.SaveSettingsAsync(new PurchaseSettings(100m));

        _a.Context.Role = ErpPermissions.Purchasing;
        await Assert.ThrowsAsync<ErpValidationException>(() =>
            _a.Erp.Purchases.CreateAsync(_a.Supplier, _a.Central, [new DocumentLine(_a.Coffee, 40, 5m)], "PEN"));
        var number = await _a.Erp.Payables.CreateRequestAsync(_a.Central, null, [new PurchaseRequestLine(_a.Coffee, 40, 5m)], "PEN", "Para la campaña");
        var request = Assert.Single(await _a.Erp.Payables.GetRequestsAsync());
        Assert.Equal((number, PurchaseRequestStatuses.Pending, 200m), (request.Number, request.Status, request.Total));
        await Assert.ThrowsAnyAsync<ErpValidationException>(() => _a.Erp.Payables.ApproveRequestAsync(request.Id));
        await Assert.ThrowsAsync<ErpValidationException>(() => _a.Erp.Payables.OrderRequestAsync(request.Id, _a.Supplier));

        _a.Context.Role = ErpPermissions.Accountant;
        await _a.Erp.Payables.ApproveRequestAsync(request.Id);
        _a.Context.Role = ErpPermissions.Purchasing;
        var po = await _a.Erp.Payables.OrderRequestAsync(request.Id, _a.Supplier);
        request = Assert.Single(await _a.Erp.Payables.GetRequestsAsync());
        Assert.Equal((PurchaseRequestStatuses.Ordered, po), (request.Status, request.PurchaseOrderNumber));
        Assert.Contains(await _a.OutboxAsync(), e => e.EventType == SuiteEvents.PurchaseOrderCreated && e.SubjectId == po);

        // Por debajo del umbral queda aprobada al pedirla.
        await _a.Erp.Payables.CreateRequestAsync(_a.Central, null, [new PurchaseRequestLine(_a.Coffee, 2, 5m)], "PEN");
        Assert.Equal(PurchaseRequestStatuses.Approved, (await _a.Erp.Payables.GetRequestsAsync(PurchaseRequestStatuses.Approved)).Single().Status);
    }

    [Fact]
    public async Task La_factura_que_no_cuadra_con_la_orden_y_lo_recibido_queda_retenida()
    {
        var (po, number) = await OrderAsync(10, 5m);
        await ReceiveAsync(po, 6);

        // 6 a 5.00: cuadra (6 recibidos). IGV 18 % sobre el café.
        var first = await _a.Erp.Payables.RegisterInvoiceAsync(new SupplierInvoiceDraft(_a.Supplier, po, "f001-123", new DateOnly(2026, 11, 2), null,
            "PEN", [new SupplierInvoiceLine(_a.Coffee, 6, 5m)]));
        var inv = (await _a.Erp.Payables.GetInvoicesAsync()).Single(i => i.Id == first);
        Assert.Equal((SupplierInvoiceStatuses.Approved, 30m, 5.40m, 35.40m, "F001-123"), (inv.Status, inv.Subtotal, inv.Tax, inv.Total, inv.DocumentNumber));
        await Assert.ThrowsAsync<ErpValidationException>(() => _a.Erp.Payables.RegisterInvoiceAsync(new SupplierInvoiceDraft(_a.Supplier, po,
            "F001-123", new DateOnly(2026, 11, 2), null, "PEN", [new SupplierInvoiceLine(_a.Coffee, 1, 5m)])));

        // 4 más a 5.50 (+10 %): ya se facturarían 10 y solo se recibieron 6, y el costo no cuadra.
        var second = await _a.Erp.Payables.RegisterInvoiceAsync(new SupplierInvoiceDraft(_a.Supplier, po, "F001-124", new DateOnly(2026, 11, 2), null,
            "PEN", [new SupplierInvoiceLine(_a.Coffee, 4, 5.5m)]));
        var held = (await _a.Erp.Payables.GetInvoicesAsync("held")).Single();
        Assert.Equal(second, held.Id);
        Assert.Equal(2, held.MatchNote!.Split('\n').Length);
        await Assert.ThrowsAsync<ErpValidationException>(() => _a.Erp.Payables.RegisterPaymentAsync(second, 10m, new DateOnly(2026, 11, 3), PaymentMethods.Transfer));

        await _a.Erp.Payables.ReleaseInvoiceAsync(second, "El proveedor subió el precio; lo resto llega el lunes");
        await _a.Erp.Payables.RegisterPaymentAsync(second, 10m, new DateOnly(2026, 11, 3), PaymentMethods.Transfer, "OP-991");
        await Assert.ThrowsAsync<ErpValidationException>(() => _a.Erp.Payables.RegisterPaymentAsync(second, 100m, new DateOnly(2026, 11, 3), PaymentMethods.Transfer));
        await Assert.ThrowsAsync<ErpValidationException>(() => _a.Erp.Payables.VoidInvoiceAsync(second));

        var payable = Assert.Single(await _a.Erp.Payables.GetPayablesAsync());
        Assert.Equal((2, 35.40m + 25.96m - 10m), (payable.Invoices, payable.Total));
    }

    [Fact]
    public async Task Facturas_pagos_y_cancelaciones_se_ven_en_los_demas_equipos_del_erp()
    {
        var (po, _) = await OrderAsync(10, 5m);
        foreach (var e in (await _a.OutboxAsync()).Where(e => e.EventType == SuiteEvents.PurchaseOrderCreated))
            await _b.Erp.SyncHandlers.HandlePurchaseOrderCreatedAsync(TestWorkspace.AsEvent(e));
        await ReceiveAsync(po, 10);

        var invoice = await _a.Erp.Payables.RegisterInvoiceAsync(new SupplierInvoiceDraft(_a.Supplier, po, "F001-9", new DateOnly(2026, 11, 2), null,
            "PEN", [new SupplierInvoiceLine(_a.Coffee, 10, 5m)]));
        await _a.Erp.Payables.RegisterPaymentAsync(invoice, 20m, new DateOnly(2026, 11, 3), PaymentMethods.Cash);
        foreach (var e in (await _a.OutboxAsync()).Where(e => e.EventType == SuiteEvents.RecordUpserted))
            await _b.Erp.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(e));

        // El otro equipo paga otra parte sin conexión: los dos pagos suman.
        _b.Clock.Advance(TimeSpan.FromMinutes(5));
        await _b.Erp.Payables.RegisterPaymentAsync(invoice, 10m, new DateOnly(2026, 11, 4), PaymentMethods.Transfer);
        foreach (var e in (await _b.OutboxAsync()).Where(e => e.EventType == SuiteEvents.RecordUpserted))
            await _a.Erp.SyncHandlers.HandleRecordUpsertedAsync(TestWorkspace.AsEvent(e));
        Assert.Equal(30m, (await _a.Erp.Payables.GetInvoicesAsync()).Single().Paid);
        Assert.Equal(2, (await _b.Erp.Payables.GetPaymentsAsync(invoice)).Count);

        // Cancelar una orden recibida en parte se publica y llega al otro equipo.
        var (other, number) = await OrderAsync(8, 5m);
        await ReceiveAsync(other, 3);
        foreach (var e in (await _a.OutboxAsync()).Where(e => e.EventType == SuiteEvents.PurchaseOrderCreated && e.SubjectId == number))
            await _b.Erp.SyncHandlers.HandlePurchaseOrderCreatedAsync(TestWorkspace.AsEvent(e));
        await _a.Erp.Purchases.CancelAsync(other, "El proveedor no tiene más");
        var cancelled = (await _a.OutboxAsync()).Single(e => e.EventType == SuiteEvents.PurchaseOrderCancelled);
        Assert.Equal("El proveedor no tiene más", cancelled.Payload!.Value.GetProperty("reason").GetString());
        await _b.Erp.SyncHandlers.HandlePurchaseOrderCancelledAsync(TestWorkspace.AsEvent(cancelled));
        Assert.Equal(PurchaseStatuses.Cancelled, (await _b.Erp.Purchases.GetPurchasesAsync(number, null, 1, 1)).Items.Single().Status);
    }
}
