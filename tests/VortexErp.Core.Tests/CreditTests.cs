using System.Text.Json;
using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;

namespace VortexErp.Core.Tests;

/// <summary>Fase 3a de la hoja de ruta: plazo de pago, límite de crédito con aprobación y descuento por cliente.</summary>
public sealed class CreditTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    /// <summary>El cliente tal como llega del hub, con dirección (que no se debe perder) y sus condiciones.</summary>
    private static VortexMasterChange<VortexParty> WithCredit(Guid id, VortexPartyCredit? credit, int version = 2) =>
        new(version, id, VortexChangeOperation.Updated, version, DateTimeOffset.UtcNow, "HUB",
            new VortexParty(id, true, false, "Company", "Bodega Don Pepe", null, "RUC", "20123456789", null, "999 000 111",
                new VortexAddress("Av. Perú 123", "Breña", "Lima", null, null, "PE"), "Cliente antiguo", true,
                new VortexMasterMeta(version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null), credit,
                new VortexPartyAccount(Guid.NewGuid(), "Ana (CRM)")));

    private async Task<OrderRow> OrderAsync(string number) =>
        (await _t.Erp.Orders.GetOrdersAsync(number, null, 1, 5)).Items.Single();

    [Fact]
    public async Task Las_condiciones_llegan_del_hub_y_al_cambiarlas_se_devuelve_el_cliente_completo()
    {
        await _t.Erp.Master.ApplyPartyAsync(WithCredit(_t.Customer, new VortexPartyCredit(30, 1000m, 5m)));
        var credit = (await _t.Erp.Master.GetCreditAsync(_t.Customer))!;
        Assert.Equal(new CreditTerms(30, 1000m, 5m), credit.Terms);
        Assert.Equal(1000m, credit.Available);

        await _t.Erp.Master.SaveCreditTermsAsync(_t.Customer, new CreditTerms(45, 2000m, 0));
        var upsert = (await _t.OutboxAsync()).Last(e => e.Kind == VortexOutboxKind.MasterUpsert);
        var body = upsert.Payload!.Value;
        Assert.Equal(45, body.GetProperty("credit").GetProperty("paymentTermDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("credit").GetProperty("discountPercent").ValueKind);
        // No se pierde nada de lo que el ERP no maneja, y el responsable del CRM no se manda (queda como esté).
        Assert.Equal("Av. Perú 123", body.GetProperty("address").GetProperty("line").GetString());
        Assert.Equal("Cliente antiguo", body.GetProperty("notes").GetString());
        Assert.False(body.TryGetProperty("account", out _));
        Assert.False(body.TryGetProperty("meta", out _));

        // Un cajero/facturador no cambia el crédito.
        _t.Context.Role = ErpPermissions.Billing;
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Master.SaveCreditTermsAsync(_t.Customer, new CreditTerms(0, null, 0)));
    }

    [Fact]
    public async Task El_descuento_del_cliente_se_aplica_al_precio_de_lista_y_su_plazo_al_facturar()
    {
        await _t.Erp.Master.ApplyPartyAsync(WithCredit(_t.Customer, new VortexPartyCredit(45, null, 10m)));
        var number = await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 2), new DocumentLine(_t.Bread, 10, 0.40m)], "PEN");
        var lines = await _t.Erp.Orders.GetLinesAsync((await OrderAsync(number)).Id);
        Assert.Equal([16.65m, 0.40m], lines.Select(l => l.UnitPrice));   // 18.50 − 10 %; el precio escrito se respeta
        Assert.Equal(45, await _t.Erp.Invoices.GetTermDaysAsync(_t.Customer));
        Assert.Equal(Services.InvoiceService.DefaultCreditDays, await _t.Erp.Invoices.GetTermDaysAsync(_t.Person));
    }

    [Fact]
    public async Task Un_pedido_que_supera_el_limite_queda_por_aprobar_y_no_se_factura_hasta_aprobarlo()
    {
        await _t.Erp.Master.ApplyPartyAsync(WithCredit(_t.Customer, new VortexPartyCredit(30, 100m, null)));
        var dentro = await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 4)], "PEN");   // 74.00
        Assert.False((await OrderAsync(dentro)).PendingApproval);

        // 74 + 37 = 111 > 100: el segundo queda por aprobar.
        var fuera = await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 2)], "PEN");
        var order = await OrderAsync(fuera);
        Assert.True(order.PendingApproval);
        Assert.False(order.CanInvoice);
        var created = (await _t.OutboxAsync()).Last(e => e.EventType == SuiteEvents.OrderCreated);
        Assert.Equal("pending", created.Payload!.Value.GetProperty("approvalStatus").GetString());
        Assert.Single((await _t.Erp.Orders.GetOrdersAsync(null, "pending_approval", 1, 10)).Items);
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice, 30));

        // Lo pendiente no cuenta como deuda: el disponible sigue siendo 26.
        Assert.Equal(26m, (await _t.Erp.Master.GetCreditAsync(_t.Customer))!.Available);

        // El facturador no aprueba; el contador sí, y viaja como order.approved.
        _t.Context.Role = ErpPermissions.Billing;
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.Orders.ApproveAsync(order.Id));
        _t.Context.Role = ErpPermissions.Accountant;
        await _t.Erp.Orders.ApproveAsync(order.Id);
        order = await OrderAsync(fuera);
        Assert.Equal((false, true, "Luis"), (order.PendingApproval, order.CanInvoice, order.ApprovedBy));
        Assert.Contains(await _t.OutboxAsync(), e => e.EventType == SuiteEvents.OrderApproved);
        await _t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice, 30);
    }

    [Fact]
    public async Task Otro_equipo_ve_el_pedido_por_aprobar_y_su_aprobacion()
    {
        var otro = new TestWorkspace("BBBB");
        await otro.InitializeAsync();
        try
        {
            await _t.Erp.Master.ApplyPartyAsync(WithCredit(_t.Customer, new VortexPartyCredit(null, 10m, null)));
            var number = await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 1)], "PEN");
            var id = (await OrderAsync(number)).Id;
            await _t.Erp.Orders.ApproveAsync(id);

            foreach (var entry in (await _t.OutboxAsync()).Where(e => e.Kind == VortexOutboxKind.Event))
            {
                var e = TestWorkspace.AsEvent(entry);
                if (e.Type == SuiteEvents.OrderCreated)
                {
                    await otro.Erp.SyncHandlers.HandleOrderCreatedAsync(e);
                    Assert.True((await otro.Erp.Orders.GetAsync(id))!.PendingApproval);
                }
                else if (e.Type == SuiteEvents.OrderApproved)
                {
                    await otro.Erp.SyncHandlers.HandleOrderApprovedAsync(e);
                    await otro.Erp.SyncHandlers.HandleOrderApprovedAsync(e);   // repetido: no pasa nada
                }
            }
            var alla = (await otro.Erp.Orders.GetAsync(id))!;
            Assert.Equal((false, "Luis"), (alla.PendingApproval, alla.ApprovedBy));
        }
        finally
        {
            await otro.DisposeAsync();
        }
    }
}
