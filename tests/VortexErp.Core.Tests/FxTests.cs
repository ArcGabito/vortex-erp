using VortexApi.Client;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;

namespace VortexErp.Core.Tests;

/// <summary>Fase 2b de la hoja de ruta: comprobantes en otra moneda con el tipo de cambio del hub y diferencia de cambio.</summary>
public sealed class FxTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();
    private static readonly Guid Laptop = Guid.Parse("22222222-0000-0000-0000-000000000009");   // US$ 100.00 con IGV

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _t.Erp.Master.ApplyItemAsync(TestWorkspace.Item(Laptop, "LAP-01", "Laptop", 100m, currency: "USD"));
        // 1 USD = 3.75 PEN (venta 3.76) desde el 1 de noviembre.
        await _t.Erp.Master.ApplyExchangeRateAsync(Rate(new DateOnly(2026, 11, 1), 3.75m, 3.76m));
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private static VortexMasterChange<VortexExchangeRate> Rate(DateOnly date, decimal rate, decimal? sell, string from = "USD", string to = "PEN",
        int version = 1, Guid? id = null)
    {
        var key = id ?? Guid.NewGuid();
        return new(version, key, VortexChangeOperation.Created, version, DateTimeOffset.UtcNow, "HUB",
            new VortexExchangeRate(key, date, from, to, rate, null, sell, "SBS",
                new VortexMasterMeta(version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));
    }

    /// <summary>Factura en dólares de una laptop: US$ 100.00 al tipo de venta del día (3.76).</summary>
    private async Task<InvoiceRow> DollarInvoiceAsync()
    {
        await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(Laptop, 1)], "USD");
        var order = (await _t.Erp.Orders.GetOrdersAsync(null, "uninvoiced", 1, 10)).Items.First();
        await _t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice);
        return (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Items.First();
    }

    [Fact]
    public async Task Factura_en_dolares_guarda_el_tipo_de_cambio_de_la_emision()
    {
        var invoice = await DollarInvoiceAsync();
        Assert.Equal("USD", invoice.CurrencyCode);
        Assert.Equal(100m, invoice.Total);
        Assert.Equal(3.76m, invoice.ExchangeRate);

        var e = (await _t.OutboxAsync()).Single(x => x.EventType == SuiteEvents.InvoiceIssued);
        Assert.Equal(3.76m, e.Payload!.Value.GetProperty("exchangeRate").GetDecimal());

        // El panel y la antigüedad de saldos van en soles, al tipo de la emisión.
        var dashboard = await _t.Erp.Reports.GetDashboardAsync();
        Assert.Equal(376m, dashboard.Receivable);
        var aging = Assert.Single(await _t.Erp.Invoices.GetAgingAsync());
        Assert.Equal(100m, aging.Total);
        Assert.Equal(376m, aging.BaseTotal);
    }

    [Fact]
    public async Task Factura_en_dolares_cobrada_en_soles_registra_la_diferencia_de_cambio()
    {
        var invoice = await DollarInvoiceAsync();
        // Ocho días después el dólar sube: 3.80 (venta 3.81).
        _t.Clock.Advance(TimeSpan.FromDays(8));
        await _t.Erp.Master.ApplyExchangeRateAsync(Rate(new DateOnly(2026, 11, 10), 3.80m, 3.81m));

        // Medio pago: S/ 190.50 son US$ 50.00.
        await _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 190.50m, PaymentMethods.Transfer, "OP-1", "PEN");
        Assert.Equal(50m, (await _t.Erp.Invoices.GetAsync(invoice.Id))!.Balance);

        // El resto: S/ 190.50 saldan los US$ 50.00 que quedan.
        await _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 190.50m, PaymentMethods.Transfer, "OP-2", "PEN");
        Assert.True((await _t.Erp.Invoices.GetAsync(invoice.Id))!.IsPaid);

        var payment = (await _t.Erp.Invoices.GetPaymentsAsync(invoice.Id, 1, 10)).Items.First();
        Assert.Equal(50m, payment.Amount);
        Assert.Equal("USD", payment.CurrencyCode);
        Assert.Equal(190.50m, payment.PaidAmount);
        Assert.Equal("PEN", payment.PaidCurrency);
        Assert.Equal(3.81m, payment.ExchangeRate);
        Assert.Equal(2.50m, payment.FxDifference);   // 190.50 − 50 × 3.76

        var e = (await _t.OutboxAsync()).Last(x => x.EventType == SuiteEvents.PaymentReceived);
        Assert.Equal(50m, e.Payload!.Value.GetProperty("amount").GetDecimal());
        Assert.Equal("USD", e.Payload!.Value.GetProperty("currencyCode").GetString());
        Assert.Equal(2.50m, e.Payload!.Value.GetProperty("fxDifference").GetDecimal());

        var fx = await _t.Erp.Invoices.GetFxSummaryAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal(5m, fx.Gains);
        Assert.Equal(0m, fx.Losses);
        Assert.Equal(2, fx.Payments);
        Assert.Equal("PEN", fx.CurrencyCode);
    }

    [Fact]
    public async Task Sin_tipo_de_cambio_no_se_vende_ni_se_cobra_en_otra_moneda()
    {
        var ex = await Assert.ThrowsAsync<ErpValidationException>(() =>
            _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(Laptop, 1)], "EUR"));
        Assert.Contains("USD a EUR", ex.Message);

        var invoice = await DollarInvoiceAsync();
        ex = await Assert.ThrowsAsync<ErpValidationException>(() =>
            _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 10m, PaymentMethods.Cash, null, "EUR"));
        Assert.Contains("PEN", ex.Message);
        // En dólares se cobra sin tipo de cambio y sin diferencia.
        await _t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 100m, PaymentMethods.Cash, null, "USD");
        Assert.Null((await _t.Erp.Invoices.GetPaymentsAsync(invoice.Id, 1, 10)).Items.Single().FxDifference);
    }

    [Fact]
    public async Task Precio_en_soles_se_convierte_con_el_inverso_y_el_tipo_mas_nuevo_gana()
    {
        // Pedido en dólares de un café de S/ 18.50: 18.50 / 3.75 = 4.93.
        await _t.Erp.Orders.CreateAsync(_t.Customer, null, [new DocumentLine(_t.Coffee, 1)], "USD");
        Assert.Equal(4.93m, (await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.First().Total);

        // Una versión vieja del mismo tipo de cambio no pisa la nueva.
        var id = Guid.NewGuid();
        await _t.Erp.Master.ApplyExchangeRateAsync(Rate(new DateOnly(2026, 11, 2), 3.70m, null, version: 2, id: id));
        await _t.Erp.Master.ApplyExchangeRateAsync(Rate(new DateOnly(2026, 11, 2), 3.99m, null, version: 1, id: id));
        var latest = Assert.Single(await _t.Erp.Master.GetLatestExchangeRatesAsync());
        Assert.Equal(3.70m, latest.Rate);
        Assert.Equal(new DateOnly(2026, 11, 2), latest.Date);
    }
}
