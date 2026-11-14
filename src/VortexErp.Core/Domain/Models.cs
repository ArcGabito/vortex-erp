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

    /// <summary>Sedes asignadas al usuario en el hub (<c>Access.SiteIds</c>). Vacío: trabaja en todas.</summary>
    IReadOnlyList<Guid> SiteIds => [];

    /// <summary>Rol efectivo del usuario en el ERP (<see cref="ErpPermissions"/>).</summary>
    string Role { get; }

    /// <summary>Módulos contratados en el hub para el ERP (<c>Access.Features</c>), p. ej. <c>facturacion_electronica</c>.</summary>
    IReadOnlyList<string> Features => [];

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

public static class OrderApprovals
{
    public const string Pending = "pending";
    public const string Approved = "approved";
}

/// <summary>Condiciones comerciales del cliente (del party del hub). Límite en la moneda de la empresa; null = sin límite.</summary>
/// <param name="PriceListId">Lista de precios del cliente (del hub); gana a la de la sede y al precio del artículo.</param>
public sealed record CreditTerms(int? PaymentTermDays, decimal? CreditLimit, decimal DiscountPercent, Guid? PriceListId = null);

/// <summary>Crédito del cliente hoy, en la moneda de la empresa: lo que debe más los pedidos abiertos sin facturar.</summary>
public sealed record CustomerCredit(Guid CustomerId, string Name, CreditTerms Terms, string CompanyCurrency, decimal Exposure, decimal Overdue)
{
    public decimal? Available => Terms.CreditLimit is { } limit ? limit - Exposure : null;
}

public static class OrderStatuses
{
    public const string Open = "open";
    public const string Dispatched = "dispatched";

    /// <summary>Salió una parte; el resto sigue pendiente en el WMS.</summary>
    public const string Partial = "partial";
    public const string Cancelled = "cancelled";
}

/// <summary>Un despacho del WMS (todo el pedido o una parte), con lo que salió y su comprobante si ya se facturó.</summary>
public sealed record ShipmentRow(Guid Id, Guid OrderId, string? Number, DateTimeOffset DispatchedAt, bool Partial, string? Carrier,
    string? TrackingNumber, Guid? InvoiceId, string? InvoiceNumber, IReadOnlyList<OrderLineRow> Lines)
{
    public decimal Total => Lines.Sum(l => l.Total);
}

/// <summary>Recordatorio de cobranza listo para enviar: comprobantes vencidos del cliente y el mensaje armado.</summary>
public sealed record CollectionReminder(Guid CustomerId, string CustomerName, string? Phone, string? Email, string CurrencyCode,
    decimal Overdue, IReadOnlyList<InvoiceRow> Invoices, string Message, DateTimeOffset? LastSentAt);

public static class ReminderChannels
{
    public const string WhatsApp = "whatsapp";
    public const string Email = "email";
}

public sealed record OrderRow(
    Guid Id, string Number, string Source, Guid? CustomerId, string? CustomerName, Guid? SiteId, string? SiteName, Guid? QuoteId,
    string Status, string CurrencyCode, decimal Total, DateTimeOffset CreatedAt, string? UserName, DateTimeOffset? DispatchedAt,
    string? ShipmentNumber, string? Carrier, string? TrackingNumber, Guid? InvoiceId, string? InvoiceNumber, int LineCount,
    string? Approval = null, string? ApprovedBy = null, int PendingShipments = 0, int InvoicedShipments = 0)
{
    /// <summary>Superó el límite de crédito del cliente: no se prepara ni se factura hasta que alguien lo apruebe.</summary>
    public bool PendingApproval => Approval == OrderApprovals.Pending;
    /// <summary>Se factura el pedido entero (si ningún despacho se facturó por separado).</summary>
    public bool CanInvoice => Status != OrderStatuses.Cancelled && InvoiceId is null && CustomerId is not null && !PendingApproval &&
                              InvoicedShipments == 0;

    /// <summary>Tiene despachos que se pueden facturar por separado.</summary>
    public bool CanInvoiceShipments => InvoiceId is null && CustomerId is not null && PendingShipments > 0;
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

/// <summary>
/// Comprobante. El saldo suma las notas de débito y resta las de crédito y lo cobrado; si queda negativo es saldo a favor
/// del cliente. Un comprobante anulado no tiene saldo.
/// </summary>
public sealed record InvoiceRow(
    Guid Id, string Number, string Kind, string Origin, Guid? CustomerId, string? CustomerName, string? CustomerDocument,
    Guid? OrderId, Guid? SaleId, DateTimeOffset IssuedAt, DateOnly? DueDate, string CurrencyCode, decimal Subtotal, decimal Tax,
    decimal Total, decimal Paid, string? UserName, string Status = InvoiceStatuses.Issued, decimal Credited = 0, decimal Debited = 0,
    DateTimeOffset? VoidedAt = null, string? VoidReason = null, decimal? ExchangeRate = null)
{
    public bool IsVoided => Status == InvoiceStatuses.Voided;
    public decimal Balance => IsVoided ? 0 : Total + Debited - Credited - Paid;
    public bool IsPaid => Balance <= 0;
    public bool IsOverdue(DateOnly today) => !IsPaid && DueDate < today;

    /// <summary>Lo que todavía se puede rebajar con notas de crédito.</summary>
    public decimal Creditable => IsVoided ? 0 : Total + Debited - Credited;

    /// <summary>Solo se anula un comprobante sin cobros ni notas: si ya tiene, se corrige con una nota de crédito.</summary>
    public bool CanVoid => !IsVoided && Paid == 0 && Credited == 0 && Debited == 0;
}

public static class InvoiceStatuses
{
    public const string Issued = "issued";
    public const string Voided = "voided";
}

public sealed record InvoiceLineRow(int LineNo, Guid? ItemId, string Description, decimal Quantity, decimal UnitPrice, decimal Total,
    string? TaxCode = null, decimal CreditedQty = 0)
{
    /// <summary>Cantidad que todavía se puede devolver con una nota de crédito.</summary>
    public decimal Returnable => Quantity - CreditedQty;
}

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
    string CurrencyCode, string Method, string? Reference, DateTimeOffset ReceivedAt, string? UserName, decimal? PaidAmount = null,
    string? PaidCurrency = null, decimal? ExchangeRate = null, decimal? FxDifference = null);

// ---------- Notas de crédito y débito ----------

public static class NoteKinds
{
    public const string Credit = "credit";
    public const string Debit = "debit";
}

/// <summary>
/// Motivos de las notas, comunes a todos los países. El paquete de localización los traduce al código fiscal de cada
/// uno (en Perú, catálogos 09 y 10 de SUNAT).
/// </summary>
public static class NoteReasons
{
    // Notas de crédito
    /// <summary>Anulación de la operación: rebaja todo lo que queda del comprobante.</summary>
    public const string Void = "void";

    /// <summary>Devolución de artículos (por línea y cantidad).</summary>
    public const string Return = "return";

    /// <summary>Descuento o bonificación después de emitir.</summary>
    public const string Discount = "discount";

    /// <summary>Corrección de un error (precio, descripción).</summary>
    public const string Correction = "correction";

    // Notas de débito
    /// <summary>Intereses por pago fuera de plazo.</summary>
    public const string Interest = "interest";

    /// <summary>Aumento del valor de la operación.</summary>
    public const string Increase = "increase";

    /// <summary>Penalidad u otro cargo.</summary>
    public const string Penalty = "penalty";

    public static readonly string[] CreditReasons = [Void, Return, Discount, Correction];
    public static readonly string[] DebitReasons = [Interest, Increase, Penalty];
}

/// <summary>
/// Línea de una nota. Con <c>InvoiceLineNo</c> devuelve esa cantidad de la línea del comprobante (al mismo precio); sin
/// ella es un importe libre (descuento, corrección, interés) con impuesto incluido donde los precios lo incluyen.
/// </summary>
public sealed record NoteLineDraft(int? InvoiceLineNo = null, decimal Quantity = 0, decimal Amount = 0, string? Description = null);

public sealed record InvoiceNoteRow(
    Guid Id, string Number, string Kind, string Origin, Guid InvoiceId, string? InvoiceNumber, Guid? CustomerId, string? CustomerName,
    string Reason, string? Description, DateTimeOffset IssuedAt, string CurrencyCode, decimal Subtotal, decimal Tax, decimal Total,
    string? UserName);

// ---------- Antigüedad de saldos y estado de cuenta ----------

/// <summary>
/// Saldo de un cliente en una moneda repartido por días de vencido: aún no vence, 1-30, 31-60, 61-90 y más de 90.
/// </summary>
public sealed record AgingRow(
    Guid? CustomerId, string CustomerName, string CurrencyCode, decimal Current, decimal Days1To30, decimal Days31To60,
    decimal Days61To90, decimal Over90, decimal BaseTotal = 0, decimal BaseOverdue = 0)
{
    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90;
    public decimal Overdue => Total - Current;
}

public static class StatementEntryKinds
{
    public const string Invoice = "invoice";
    public const string CreditNote = "credit_note";
    public const string DebitNote = "debit_note";
    public const string Payment = "payment";
}

/// <summary>Movimiento del estado de cuenta: cargo (comprobante, nota de débito) o abono (cobro, nota de crédito).</summary>
public sealed record StatementEntry(
    DateTimeOffset At, string Kind, Guid DocumentId, string Number, string? Reference, DateOnly? DueDate, decimal Charge,
    decimal Credit, decimal Balance);

/// <summary>Estado de cuenta de un cliente en una moneda, entre dos fechas, con el saldo anterior y el final.</summary>
public sealed record CustomerStatement(
    Guid CustomerId, string CustomerName, string? CustomerDocument, string CurrencyCode, DateOnly From, DateOnly To,
    decimal OpeningBalance, IReadOnlyList<StatementEntry> Entries, decimal ClosingBalance, decimal Overdue, DateTimeOffset GeneratedAt);

/// <summary>Saldo por cliente: total pendiente y cuánto está vencido.</summary>
public sealed record ReceivableRow(Guid? CustomerId, string CustomerName, int Invoices, decimal Balance, decimal Overdue, DateOnly? OldestDue);

public sealed record PosSaleRow(
    Guid Id, string Number, Guid? SiteId, string? SiteName, Guid? CustomerId, string? CustomerName, string Status,
    string CurrencyCode, decimal Total, decimal Credit, decimal Returned, DateTimeOffset CreatedAt, Guid? InvoiceId,
    string? InvoiceNumber, string? FiscalNumber = null)
{
    /// <summary>Si el ERP puede emitirle comprobante: no si la caja ya emitió el suyo electrónico (<see cref="FiscalNumber"/>).</summary>
    public bool CanInvoice => Status == "completed" && InvoiceId is null && FiscalNumber is null;
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

// ---------- Panel: tendencias, categorías y transacciones ----------

/// <summary>Tamaño de cada periodo de una serie del panel.</summary>
public enum TrendGrain
{
    Day,
    Week,
    Month,
}

/// <summary>
/// Un periodo de la serie del panel. Ingresos = comprobantes de pedidos + ventas de caja netas de devoluciones.
/// Clientes nuevos = los que hicieron su primera compra (comprobante o venta de caja) en el periodo.
/// </summary>
public sealed record TrendPoint(DateOnly Start, decimal Invoiced, decimal PosSales, int Orders, int NewCustomers, int Documents)
{
    public decimal Revenue => Invoiced + PosSales;
    public decimal AverageTicket => Documents == 0 ? 0 : Math.Round(Revenue / Documents, 2);
}

/// <summary>Ingresos de una categoría de artículos en el periodo y en el periodo anterior del mismo largo.</summary>
public sealed record CategoryRevenue(string Category, decimal Current, decimal Previous)
{
    public decimal? ChangePct => Previous > 0 ? Math.Round((Current - Previous) / Previous * 100, 1) : null;
}

public static class TransactionStatuses
{
    public const string Paid = "paid";
    public const string Partial = "partial";
    public const string Pending = "pending";
    public const string Overdue = "overdue";
    public const string Voided = "voided";
}

/// <summary>Comprobante reciente para la tabla del panel.</summary>
public sealed record RecentTransaction(
    Guid Id, string Number, string Kind, string? CustomerName, string? FirstLine, int Lines, decimal Quantity,
    decimal Total, decimal Balance, string Status, DateTimeOffset IssuedAt, string CurrencyCode);

// ---------- Multimoneda ----------

/// <summary>Tipo de cambio del hub: 1 <c>From</c> = <c>Rate</c> <c>To</c> (con compra y venta si las trae).</summary>
public sealed record ExchangeRateRow(DateOnly Date, string From, string To, decimal Rate, decimal? BuyRate, decimal? SellRate, string? Source);

/// <summary>Diferencia de cambio de los cobros del periodo, en moneda de la empresa: ganancias, pérdidas y neto.</summary>
public sealed record FxSummary(string CurrencyCode, decimal Gains, decimal Losses, int Payments)
{
    public decimal Net => Gains - Losses;
}

// ---------- Listas de precios (ERP-07) ----------

public sealed record PriceListRow(Guid Id, string Code, string Name, string CurrencyCode, DateOnly? ValidFrom, DateOnly? ValidTo,
    IReadOnlyList<Guid> SiteIds, bool IsActive, bool Pending, int Items, int Customers);

/// <summary>Precio de un artículo desde una cantidad (escala).</summary>
public sealed record PriceListLineRow(Guid ItemId, decimal Price, decimal MinQuantity = 1);

public sealed record PriceListDraft(string Code, string Name, string CurrencyCode, DateOnly? ValidFrom, DateOnly? ValidTo,
    IReadOnlyList<Guid> SiteIds, bool IsActive, IReadOnlyList<PriceListLineRow> Lines);

/// <summary>
/// Cómo se pone precio a un pedido: la lista que rige (del cliente o de la sede, ya en la moneda del pedido) y el descuento
/// habitual del cliente. Un artículo de la lista va a su precio (por escala); uno que no está, al precio del artículo con
/// el descuento habitual (el descuento no se suma a la lista).
/// </summary>
public sealed class PriceBook(Guid? listId, string? listName, string currency, decimal discountPercent, decimal rate,
    IReadOnlyDictionary<Guid, IReadOnlyList<(decimal Min, decimal Price)>> lines)
{
    public Guid? ListId { get; } = listId;
    public string? ListName { get; } = listName;
    public string Currency { get; } = currency;
    public decimal DiscountPercent { get; } = discountPercent;

    /// <summary>Precio de la lista para esa cantidad (las fracciones toman la escala "desde 1"), o null si no está.</summary>
    public decimal? ListPrice(Guid itemId, decimal quantity) =>
        lines.TryGetValue(itemId, out var steps) && steps.LastOrDefault(s => s.Min <= Math.Max(quantity, 1)) is { Min: > 0 } step
            ? VortexApi.Suite.Money.Currencies.Get(Currency).Round(step.Price * rate)
            : null;

    /// <summary>Precio sugerido: el de la lista o, si no está, <paramref name="itemPrice"/> (ya en la moneda) con el descuento.</summary>
    public decimal? PriceFor(Guid itemId, decimal quantity, decimal? itemPrice) =>
        ListPrice(itemId, quantity) ?? (itemPrice is { } price
            ? DiscountPercent > 0 ? VortexApi.Suite.Money.Currencies.Get(Currency).Round(price * (1 - DiscountPercent / 100m)) : price
            : null);
}

// ---------- Compras y cuentas por pagar (fase 4a) ----------

/// <summary>Ajustes de compras: sobre qué monto (en la moneda de la empresa) una compra necesita aprobación y cuánto puede
/// diferir el costo facturado del de la orden antes de retener la factura.</summary>
public sealed record PurchaseSettings(decimal? ApprovalThreshold, decimal PriceTolerancePercent = 2)
{
    public static PurchaseSettings Default { get; } = new(ApprovalThreshold: null);
}

public static class PurchaseRequestStatuses
{
    /// <summary>Supera el umbral: espera la aprobación.</summary>
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    /// <summary>Ya se convirtió en orden de compra.</summary>
    public const string Ordered = "ordered";
}

public sealed record PurchaseRequestLine(Guid ItemId, decimal Quantity, decimal? UnitCost = null);

public sealed record PurchaseRequestRow(
    Guid Id, string Number, Guid? SiteId, string? SiteName, Guid? SupplierId, string? SupplierName, string Status, string CurrencyCode,
    decimal Total, string? Notes, string? RequestedBy, DateTimeOffset RequestedAt, string? DecidedBy, DateTimeOffset? DecidedAt,
    string? Reason, Guid? PurchaseOrderId, string? PurchaseOrderNumber, IReadOnlyList<PurchaseLineRow> Lines);

public static class SupplierInvoiceStatuses
{
    /// <summary>No cuadra con la orden o lo recibido: no se paga hasta que alguien con permiso la libere.</summary>
    public const string Held = "held";
    public const string Approved = "approved";
    public const string Void = "void";
}

public sealed record SupplierInvoiceLine(Guid ItemId, decimal Quantity, decimal UnitCost);

/// <param name="UnitCost">Costo sin impuesto, como en la orden de compra.</param>
public sealed record SupplierInvoiceDraft(Guid SupplierId, Guid? PurchaseOrderId, string DocumentNumber, DateOnly IssuedOn, DateOnly? DueOn,
    string CurrencyCode, IReadOnlyList<SupplierInvoiceLine> Lines);

public sealed record SupplierInvoiceRow(
    Guid Id, Guid SupplierId, string? SupplierName, Guid? PurchaseOrderId, string? PurchaseOrderNumber, string DocumentNumber,
    DateOnly IssuedOn, DateOnly DueOn, string CurrencyCode, decimal Subtotal, decimal Tax, decimal Total, decimal Paid, string Status,
    string? MatchNote, string? ReleasedBy, string? ReleaseReason, DateOnly? PlannedOn, DateTimeOffset CreatedAt)
{
    public decimal Balance => Status == SupplierInvoiceStatuses.Void ? 0 : Math.Max(0, Total - Paid);
    public bool IsHeld => Status == SupplierInvoiceStatuses.Held;
}

public sealed record SupplierPaymentRow(Guid Id, Guid InvoiceId, decimal Amount, DateOnly PaidOn, string Method, string? Reference,
    DateTimeOffset CreatedAt, string? UserName);

/// <summary>Cuentas por pagar de un proveedor: lo que debe por tramo de vencimiento (moneda de cada factura).</summary>
public sealed record PayableSummary(Guid SupplierId, string SupplierName, string CurrencyCode, decimal NotDue, decimal Overdue1To30,
    decimal Overdue31To60, decimal OverdueOver60, int Invoices, int Held)
{
    public decimal Total => NotDue + Overdue1To30 + Overdue31To60 + OverdueOver60;
}
