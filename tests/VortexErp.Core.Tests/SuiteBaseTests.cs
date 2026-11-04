using VortexErp.Core.Domain;

namespace VortexErp.Core.Tests;

/// <summary>Base común de la suite en el ERP: impuestos por país, permisos y auditoría.</summary>
public sealed class SuiteBaseTests
{
    private static async Task<T> WithAsync<T>(TestWorkspace t, Func<Task<T>> test)
    {
        await t.InitializeAsync();
        try
        {
            return await test();
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task Factura_mexicana_con_IVA_16_y_pan_exento()
    {
        var t = new TestWorkspace(country: "MX", currency: "MXN");
        await WithAsync(t, async () =>
        {
            await t.Erp.Orders.CreateAsync(t.Customer, null, [new DocumentLine(t.Coffee, 10, 116m), new DocumentLine(t.Bread, 10)], "MXN");
            var order = (await t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.Single();
            await t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice);

            var invoice = (await t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Items.Single();
            Assert.Equal(1000m + 5m, invoice.Subtotal);   // 1160 / 1.16 + pan exento
            Assert.Equal(160m, invoice.Tax);
            Assert.Equal(1165m, invoice.Total);
            Assert.Equal("MXN", invoice.CurrencyCode);
            return 0;
        });
    }

    [Fact]
    public async Task En_EEUU_sin_sales_tax_configurado_la_factura_va_sin_impuesto_sumado()
    {
        var t = new TestWorkspace(country: "US", currency: "USD", taxRate: 0m);
        await WithAsync(t, async () =>
        {
            await t.Erp.Orders.CreateAsync(t.Customer, null, [new DocumentLine(t.Coffee, 4, 25m)], "USD");
            var order = (await t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.Single();
            await t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice);

            var invoice = (await t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Items.Single();
            Assert.Equal(new[] { 100m, 0m, 100m }, new[] { invoice.Subtotal, invoice.Tax, invoice.Total });
            return 0;
        });
    }

    [Fact]
    public async Task Cada_rol_hace_solo_lo_suyo()
    {
        var t = new TestWorkspace(role: ErpPermissions.Purchasing);
        await WithAsync(t, async () =>
        {
            await t.Erp.Purchases.CreateAsync(t.Supplier, null, [new DocumentLine(t.Coffee, 10, 11m)], "PEN");
            var ex = await Assert.ThrowsAsync<ErpValidationException>(() => t.Erp.Orders.CreateAsync(t.Customer, null, [new DocumentLine(t.Coffee, 1)], "PEN"));
            Assert.Contains("Tu rol (compras) no permite crear pedidos", ex.Message);

            t.Context.Role = ErpPermissions.Billing;
            await t.Erp.Orders.CreateAsync(t.Customer, null, [new DocumentLine(t.Coffee, 1)], "PEN");
            await Assert.ThrowsAsync<ErpValidationException>(() => t.Erp.Purchases.CreateAsync(t.Supplier, null, [new DocumentLine(t.Coffee, 1, 1m)], "PEN"));
            await Assert.ThrowsAsync<ErpValidationException>(() => t.Erp.Audit.GetAsync(1, 10));

            t.Context.Role = ErpPermissions.ReadOnly;   // quien entra sin rol
            var order = (await t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items.Single();
            await Assert.ThrowsAsync<ErpValidationException>(() => t.Erp.Invoices.IssueFromOrderAsync(order.Id, InvoiceKinds.Invoice));
            await Assert.ThrowsAsync<ErpValidationException>(() => t.Erp.Master.CreatePartyAsync(
                new PartyDraft("Nuevo SAC", "Company", true, false, "RUC", "20111111111", null, null, null)));
            return 0;
        });
    }

    [Fact]
    public async Task Emision_cobros_y_cancelaciones_quedan_en_la_auditoria()
    {
        var t = new TestWorkspace();
        await WithAsync(t, async () =>
        {
            await t.Erp.Orders.CreateAsync(t.Customer, null, [new DocumentLine(t.Coffee, 2)], "PEN");
            await t.Erp.Orders.CreateAsync(t.Customer, null, [new DocumentLine(t.Coffee, 1)], "PEN");
            var orders = (await t.Erp.Orders.GetOrdersAsync(null, null, 1, 10)).Items;
            await t.Erp.Invoices.IssueFromOrderAsync(orders[0].Id, InvoiceKinds.Invoice);
            var invoice = (await t.Erp.Invoices.GetInvoicesAsync(new InvoiceQuery())).Items.Single();
            await t.Erp.Invoices.RegisterPaymentAsync(invoice.Id, 10, PaymentMethods.Transfer, "OP 1");
            await t.Erp.Orders.CancelAsync(orders[1].Id);

            var log = (await t.Erp.Audit.GetAsync(1, 20)).Items;
            Assert.Equal(["document.issued", "payment.registered", "document.cancelled"], log.Select(e => e.Action).Reverse());
            Assert.Contains(log, e => e.Reference == invoice.Number && e.Detail!.StartsWith("Factura por"));
            return 0;
        });
    }
}
