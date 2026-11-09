using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexErp.Core.Domain;

namespace VortexErp.Core.Tests;

/// <summary>ERP-07: listas de precios del hub por cliente y por sede, con escalas, en los pedidos.</summary>
public sealed class PriceListsTests : IAsyncLifetime
{
    // Café S/ 18.50 y pan S/ 0.50. La sede por defecto del usuario es el almacén central.
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<OrderLineRow[]> OrderLinesAsync(params DocumentLine[] lines)
    {
        var number = await _t.Erp.Orders.CreateAsync(_t.Customer, _t.Central, lines, "PEN");
        var order = (await _t.Erp.Orders.GetOrdersAsync(number, null, 1, 1)).Items.Single();
        return (await _t.Erp.Orders.GetLinesAsync(order.Id)).ToArray();
    }

    [Fact]
    public async Task La_lista_del_cliente_pone_el_precio_con_escalas_y_lo_demas_lleva_el_descuento()
    {
        var wholesale = await _t.Erp.PriceLists.SaveAsync(null, new PriceListDraft("mayorista", "Mayorista", "PEN", null, null, [], true,
            [new PriceListLineRow(_t.Coffee, 17m), new PriceListLineRow(_t.Coffee, 15.5m, 12)]));
        await _t.Erp.Master.SaveCreditTermsAsync(_t.Customer, new CreditTerms(30, null, 10, wholesale));

        // La lista se sube entera al hub y el cliente lleva su lista en las condiciones comerciales.
        var outbox = await _t.OutboxAsync();
        var upsert = outbox.Single(e => e.Kind == VortexOutboxKind.MasterUpsert && e.Resource == VortexMasterResource.PriceLists);
        Assert.Equal(2, upsert.Payload!.Value.GetProperty("lines").GetArrayLength());
        var party = outbox.Last(e => e.Resource == VortexMasterResource.Parties).Payload!.Value;
        Assert.Equal(wholesale, party.GetProperty("credit").GetProperty("priceListId").GetGuid());

        // Café de la lista (sin el descuento habitual); 12 en dos líneas toman la escala; el pan con el 10 %.
        var lines = await OrderLinesAsync(new DocumentLine(_t.Coffee, 2), new DocumentLine(_t.Bread, 10));
        Assert.Equal([17m, 0.45m], lines.Select(l => l.UnitPrice));
        lines = await OrderLinesAsync(new DocumentLine(_t.Coffee, 10), new DocumentLine(_t.Coffee, 2));
        Assert.All(lines, l => Assert.Equal(15.5m, l.UnitPrice));

        // El precio escrito manda.
        Assert.Equal(16m, (await OrderLinesAsync(new DocumentLine(_t.Coffee, 1, 16m))).Single().UnitPrice);

        var book = await _t.Erp.PriceLists.PriceBookAsync(_t.Customer, _t.Central, "PEN");
        Assert.Equal(("Mayorista", 15.5m), (book.ListName, book.PriceFor(_t.Coffee, 20, 18.50m)!.Value));
    }

    [Fact]
    public async Task Sin_lista_del_cliente_rige_la_de_la_sede_y_se_edita_entera()
    {
        var id = await _t.Erp.PriceLists.SaveAsync(null, new PriceListDraft("central", "Almacén central", "PEN", null, null, [_t.Central], true,
            [new PriceListLineRow(_t.Coffee, 18m)]));
        Assert.Equal(18m, (await OrderLinesAsync(new DocumentLine(_t.Coffee, 1))).Single().UnitPrice);

        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.PriceLists.SaveAsync(null,
            new PriceListDraft("CENTRAL", "Otra", "PEN", null, null, [], true, [])));
        await Assert.ThrowsAsync<ErpValidationException>(() => _t.Erp.PriceLists.SaveAsync(id,
            new PriceListDraft("CENTRAL", "Almacén central", "PEN", null, null, [_t.Central], true,
                [new PriceListLineRow(_t.Coffee, 18m), new PriceListLineRow(_t.Coffee, 17m)])));

        // Inactiva o fuera de vigencia no rige.
        await _t.Erp.PriceLists.SaveAsync(id, new PriceListDraft("CENTRAL", "Almacén central", "PEN", new DateOnly(2026, 12, 1), null,
            [_t.Central], true, [new PriceListLineRow(_t.Coffee, 18m)]));
        Assert.Equal(18.50m, (await OrderLinesAsync(new DocumentLine(_t.Coffee, 1))).Single().UnitPrice);
        var row = Assert.Single(await _t.Erp.PriceLists.GetListsAsync());
        Assert.True(row.Pending);

        // Lo que vuelve del hub (versión nueva) queda confirmado.
        var draft = (await _t.Erp.PriceLists.GetAsync(id))!;
        await _t.Erp.Master.ApplyPriceListAsync(new VortexMasterChange<VortexPriceList>(1, id, VortexChangeOperation.Updated, 1,
            DateTimeOffset.UtcNow, "HUB", new VortexPriceList(id, draft.Code, draft.Name, "PEN", draft.ValidFrom, null, draft.SiteIds, true,
                draft.Lines.Select(l => new VortexPriceListLine(l.ItemId, l.Price, l.MinQuantity)).ToList(),
                new VortexMasterMeta(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null))));
        Assert.False(Assert.Single(await _t.Erp.PriceLists.GetListsAsync()).Pending);
    }
}
