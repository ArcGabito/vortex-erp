using VortexErp.Core.Domain;
using VortexErp.Core.Integration;
using VortexErp.Core.Services;

namespace VortexErp.Core.Tests;

/// <summary>Datos del panel: series por periodo, ingresos por categoría y transacciones recientes.</summary>
public sealed class DashboardTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<Guid> InvoiceAsync(Guid customer, decimal coffees, int creditDays = 0)
    {
        await _t.Erp.Orders.CreateAsync(customer, null, [new DocumentLine(_t.Coffee, coffees)], "PEN");
        var order = (await _t.Erp.Orders.GetOrdersAsync(null, null, 1, 50)).Items.First(o => o.InvoiceId is null);
        await _t.Erp.Invoices.IssueFromOrderAsync(order.Id, customer == _t.Customer ? InvoiceKinds.Invoice : InvoiceKinds.Receipt, creditDays: creditDays);
        return (await _t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Items.First().Id;
    }

    private Task PosSaleAsync(decimal total) => _t.Erp.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new
    {
        saleId = Guid.NewGuid(), number = $"V-{Guid.NewGuid():N}"[..10], siteId = _t.Store, currencyCode = "PEN",
        subtotal = total, tax = 0m, total,
        lines = new[] { new { itemId = _t.Bread, sku = "PAN-01", quantity = total / 0.5m, unitPrice = 0.50m, discount = 0m, total, taxable = false } },
        payments = new[] { new { method = "cash", amount = total } },
    }, "POS") with { OccurredAt = _t.Clock.GetUtcNow() });

    [Fact]
    public void Las_semanas_empiezan_el_lunes_y_los_meses_el_dia_1()
    {
        var friday = new DateOnly(2026, 11, 6);
        Assert.Equal(new DateOnly(2026, 11, 2), ReportService.PeriodStart(friday, TrendGrain.Week));
        Assert.Equal(new DateOnly(2026, 11, 1), ReportService.PeriodStart(friday, TrendGrain.Month));
        Assert.Equal(new DateOnly(2026, 11, 9), ReportService.NextPeriod(new DateOnly(2026, 11, 2), TrendGrain.Week));
    }

    [Fact]
    public async Task La_serie_mensual_separa_facturado_y_caja_y_cuenta_clientes_nuevos_una_sola_vez()
    {
        // Noviembre: Don Pepe compra por primera vez. Diciembre: vuelve (no es nuevo), Rosa es nueva, y una venta de caja.
        await InvoiceAsync(_t.Customer, 2);
        _t.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 3, 15, 0, 0, TimeSpan.Zero));
        await InvoiceAsync(_t.Customer, 1);
        await InvoiceAsync(_t.Person, 4);
        await PosSaleAsync(10m);

        var trend = await _t.Erp.Reports.GetTrendAsync(TrendGrain.Month, 3);
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)], trend.Select(p => p.Start));
        Assert.Equal([0m, 37m, 92.5m], trend.Select(p => p.Invoiced));
        Assert.Equal(10m, trend[2].PosSales);
        Assert.Equal([0, 1, 2], trend.Select(p => p.Orders));
        Assert.Equal([0, 1, 1], trend.Select(p => p.NewCustomers));
        Assert.Equal(3, trend[2].Documents);
        Assert.Equal(34.17m, trend[2].AverageTicket);   // (92.50 + 10) / 3
    }

    [Fact]
    public async Task Ingresos_por_categoria_se_comparan_con_el_periodo_anterior()
    {
        await InvoiceAsync(_t.Customer, 1);                 // 18.50 de Abarrotes en noviembre
        _t.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 3, 15, 0, 0, TimeSpan.Zero));
        await InvoiceAsync(_t.Customer, 2);                 // 37 en diciembre
        await PosSaleAsync(5m);

        var rows = await _t.Erp.Reports.GetRevenueByCategoryAsync(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31));
        var groceries = Assert.Single(rows);
        Assert.Equal("Abarrotes", groceries.Category);
        Assert.Equal(42m, groceries.Current);
        Assert.Equal(18.5m, groceries.Previous);
        Assert.Equal(127m, groceries.ChangePct);
    }

    [Fact]
    public async Task Transacciones_recientes_muestran_estado_de_cobro()
    {
        var paid = await InvoiceAsync(_t.Customer, 1);
        await _t.Erp.Invoices.RegisterPaymentAsync(paid, 18.50m, PaymentMethods.Cash, null);
        var partial = await InvoiceAsync(_t.Customer, 2, creditDays: 30);
        await _t.Erp.Invoices.RegisterPaymentAsync(partial, 10m, PaymentMethods.Cash, null);
        await InvoiceAsync(_t.Customer, 3, creditDays: 5);
        _t.Clock.Advance(TimeSpan.FromDays(10));

        var rows = await _t.Erp.Reports.GetRecentTransactionsAsync();
        Assert.Equal(3, rows.Count);
        var byTotal = rows.ToDictionary(r => r.Total, r => r);
        Assert.Equal(TransactionStatuses.Paid, byTotal[18.5m].Status);
        Assert.Equal(TransactionStatuses.Partial, byTotal[37m].Status);
        Assert.Equal(27m, byTotal[37m].Balance);
        Assert.Equal(TransactionStatuses.Overdue, byTotal[55.5m].Status);
        Assert.Equal("Bodega Don Pepe", rows[0].CustomerName);
        Assert.Equal(3m, byTotal[55.5m].Quantity);

        Assert.Single(await _t.Erp.Reports.GetRecentTransactionsAsync(rows[0].Number));
    }
}
