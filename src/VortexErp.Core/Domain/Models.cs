namespace VortexErp.Core.Domain;

/// <summary>Quién opera y en qué equipo. Lo provee la app a partir de la sesión del hub.</summary>
public interface IErpContext
{
    Guid? UserId { get; }
    string UserName { get; }

    /// <summary>Prefijo corto del equipo para numerar documentos sin choques entre equipos sin conexión.</summary>
    string DeviceTag { get; }

    /// <summary>Sede por defecto de los pedidos y compras (puede ser null).</summary>
    Guid? DefaultSiteId { get; }

    /// <summary>Rol efectivo del usuario en el ERP (<see cref="ErpPermissions"/>).</summary>
    string Role { get; }

    /// <summary>Si el usuario puede hacer la acción (<see cref="ErpPermissions"/>).</summary>
    bool Can(string permission);
}

/// <summary>Dato inválido o regla de negocio incumplida: se muestra tal cual al usuario.</summary>
public sealed class ErpValidationException(string message) : Exception(message);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

// ---------- Datos maestros (copia local del hub) ----------

/// <summary><see cref="OnHand"/> es el stock que informa el WMS (null si no informó nada del artículo).</summary>
public sealed record ItemRow(
    Guid Id, string Sku, string Name, string Kind, string? Description, string Unit, string? Barcode, string? Category,
    decimal Price, string CurrencyCode, string? TaxCode, bool TrackInventory, bool IsActive, int Version, bool Pending,
    decimal? OnHand);

public sealed record SiteRow(
    Guid Id, string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string? CountryCode,
    bool IsActive, int Version, bool Pending, int OpenOrders, int OpenPurchases);

public sealed record PartyRow(
    Guid Id, string Name, string? TradeName, string Kind, string? DocumentType, string? DocumentNumber, bool IsCustomer,
    bool IsSupplier, string? Email, string? Phone, string? City, bool IsActive, int Version, bool Pending);

/// <summary>Empresa del hub. <c>DefaultTaxRate</c> en porcentaje (18 = 18 %) y <c>TimeZone</c> IANA.</summary>
public sealed record CompanyInfo(
    string LegalName, string? TradeName, string? TaxId, string CountryCode, string CurrencyCode, decimal? DefaultTaxRate = null,
    string? TimeZone = null);

public sealed record Option(Guid Id, string Label, string? Detail = null);

public sealed record ItemDraft(
    string Sku, string Name, string Unit, string? Category, string? Barcode, decimal Price, string CurrencyCode,
    string? Description = null, bool TrackInventory = true, string Kind = "Product", string? TaxCode = VortexApi.Suite.Taxes.TaxCategories.Standard, bool IsActive = true);

public sealed record SiteDraft(
    string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string CountryCode, bool IsActive = true);

public sealed record PartyDraft(
    string Name, string Kind, bool IsCustomer, bool IsSupplier, string? DocumentType, string? DocumentNumber,
    string? Email, string? Phone, string? City, string CountryCode = "PE");

/// <summary>
/// Línea de un documento. En ventas el precio es como el de lista del hub: con impuesto donde los precios lo incluyen
/// (Perú, LATAM, Europa) y sin él en EE. UU. En compras es el costo.
/// </summary>
public sealed record DocumentLine(Guid ItemId, decimal Quantity, decimal? UnitPrice = null);

// ---------- Impuestos ----------

/// <summary>Redondeo a céntimos de los importes de línea (los totales del documento usan la moneda: <c>VortexApi.Suite</c>).</summary>
public static class Tax
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

// ---------- Pedidos de venta ----------

public static class OrderStatuses
{
    public const string Open = "open";
    public const string Dispatched = "dispatched";
    public const string Cancelled = "cancelled";
}

public sealed record OrderRow(
    Guid Id, string Number, string Source, Guid? CustomerId, string? CustomerName, Guid? SiteId, string? SiteName, Guid? QuoteId,
    string Status, string CurrencyCode, decimal Total, DateTimeOffset CreatedAt, string? UserName, DateTimeOffset? DispatchedAt,
    string? ShipmentNumber, string? Carrier, string? TrackingNumber, Guid? InvoiceId, string? InvoiceNumber, int LineCount)
{
    public bool CanInvoice => Status != OrderStatuses.Cancelled && InvoiceId is null && CustomerId is not null;
    public bool CanCancel => Status == OrderStatuses.Open && InvoiceId is null;
}

public sealed record OrderLineRow(int LineNo, Guid ItemId, string Sku, string ItemName, decimal Quantity, decimal UnitPrice)
{
    public decimal Total => Tax.Round(Quantity * UnitPrice);
}

public sealed record QuoteRow(
    Guid Id, string? Number, Guid? CustomerId, string? CustomerName, string CurrencyCode, decimal Total, DateTimeOffset AcceptedAt,
    Guid? OrderId, string? OrderNumber, int LineCount);

// ---------- Comprobantes y cobranzas ----------

public static class InvoiceKinds
{
    /// <summary>Factura: a empresas con RUC (permite crédito fiscal).</summary>
    public const string Invoice = "factura";

    /// <summary>Boleta de venta: a personas o consumidores finales.</summary>
    public const string Receipt = "boleta";
}

public sealed record InvoiceRow(
    Guid Id, string Number, string Kind, string Origin, Guid? CustomerId, string? CustomerName, string? CustomerDocument,
    Guid? OrderId, Guid? SaleId, DateTimeOffset IssuedAt, DateOnly? DueDate, string CurrencyCode, decimal Subtotal, decimal Tax,
    decimal Total, decimal Paid, string? UserName)
{
    public decimal Balance => Total - Paid;
    public bool IsPaid => Balance <= 0;
    public bool IsOverdue(DateOnly today) => !IsPaid && DueDate < today;
}

public sealed record InvoiceLineRow(int LineNo, Guid? ItemId, string Description, decimal Quantity, decimal UnitPrice, decimal Total);

public sealed record InvoiceQuery(
    string? Search = null, string? Kind = null, bool? Unpaid = null, Guid? CustomerId = null, int Page = 1, int PageSize = 15);

public static class PaymentMethods
{
    public const string Cash = "cash";
    public const string Card = "card";
    public const string Transfer = "transfer";
    public const string Wallet = "wallet";
    public const string Check = "check";

    public static readonly string[] All = [Transfer, Cash, Card, Wallet, Check];
}

public sealed record PaymentRow(
    Guid Id, string Number, Guid? CustomerId, string? CustomerName, Guid? InvoiceId, string? InvoiceNumber, decimal Amount,
    string CurrencyCode, string Method, string? Reference, DateTimeOffset ReceivedAt, string? UserName);

/// <summary>Saldo por cliente: total pendiente y cuánto está vencido.</summary>
public sealed record ReceivableRow(Guid? CustomerId, string CustomerName, int Invoices, decimal Balance, decimal Overdue, DateOnly? OldestDue);

public sealed record PosSaleRow(
    Guid Id, string Number, Guid? SiteId, string? SiteName, Guid? CustomerId, string? CustomerName, string Status,
    string CurrencyCode, decimal Total, decimal Credit, decimal Returned, DateTimeOffset CreatedAt, Guid? InvoiceId,
    string? InvoiceNumber)
{
    public bool CanInvoice => Status == "completed" && InvoiceId is null;
}

// ---------- Compras ----------

public static class PurchaseStatuses
{
    public const string Open = "open";
    public const string Partial = "partial";
    public const string Received = "received";
    public const string Cancelled = "cancelled";
}

public sealed record PurchaseRow(
    Guid Id, string Number, Guid SupplierId, string? SupplierName, Guid SiteId, string? SiteName, string Status,
    string CurrencyCode, decimal Total, DateTimeOffset CreatedAt, DateOnly? ExpectedAt, DateTimeOffset? ReceivedAt,
    int LineCount, decimal Ordered, decimal Received);

public sealed record PurchaseLineRow(int LineNo, Guid ItemId, string Sku, string ItemName, decimal Quantity, decimal UnitCost, decimal ReceivedQty)
{
    public decimal Total => Tax.Round(Quantity * UnitCost);
}

// ---------- Inventario ----------

/// <summary>Stock que informa el WMS, valorizado al último costo de compra (o al precio de venta si no hubo compras).</summary>
public sealed record StockRow(
    Guid SiteId, string SiteName, Guid ItemId, string Sku, string ItemName, string? Category, string Unit, decimal OnHand,
    decimal UnitCost, bool CostFromPurchase, DateTimeOffset UpdatedAt)
{
    public decimal Value => Tax.Round(OnHand * UnitCost);
}

// ---------- Panel ----------

public sealed record AuditRow(
    Guid Id, DateTimeOffset At, string? UserName, string? Device, string Action, string Entity, string? Reference, string? Detail);

public sealed record MonthRevenue(DateOnly Month, decimal Invoiced, decimal PosSales);

public sealed record DashboardSummary(
    decimal RevenueMonth, decimal? RevenueChangePct, decimal Receivable, decimal Overdue, int OverdueInvoices, int OrdersOpen,
    int QuotesToConvert, int PurchasesOpen, decimal InventoryValue, int PosSalesToInvoice, string CurrencyCode);
