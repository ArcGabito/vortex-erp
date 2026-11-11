namespace VortexErp.Core.Integration;

// Contratos de los eventos de la suite Vortex, versión 1 (docs/SISTEMAS.md del hub, sección 6).
// Se serializan con VortexJson.Options (camelCase). Se pueden agregar campos, nunca quitar ni cambiar su significado;
// al leer, los campos desconocidos se ignoran.

public static class SuiteEvents
{
    // Publica el ERP (y los recibe de sus otros equipos)
    public const string OrderCreated = "order.created";

    /// <summary>Un pedido que superaba el límite de crédito fue aprobado (ERP → ERP, WMS).</summary>
    public const string OrderApproved = "order.approved";
    public const string InvoiceIssued = "invoice.issued";
    public const string PaymentReceived = "payment.received";
    public const string PurchaseOrderCreated = "purchase_order.created";

    /// <summary>Orden de compra cancelada: lo recibido queda; el resto ya no se espera (WMS y otros equipos del ERP).</summary>
    public const string PurchaseOrderCancelled = "purchase_order.cancelled";

    /// <summary>Replicación interna del ERP: solicitudes de compra, facturas y pagos a proveedores, ajustes de compras.</summary>
    public const string RecordUpserted = "erp.record.upserted";
    public const string CreditNoteIssued = "credit_note.issued";
    public const string DebitNoteIssued = "debit_note.issued";
    public const string InvoiceVoided = "invoice.voided";

    // Consume el ERP
    public const string SaleCompleted = "sale.completed";
    public const string SaleVoided = "sale.voided";
    public const string SaleReturned = "sale.returned";
    public const string QuoteAccepted = "quote.accepted";
    public const string DealWon = "deal.won";
    public const string GoodsReceived = "goods.received";
    public const string StockAdjusted = "stock.adjusted";
    public const string ShipmentDispatched = "shipment.dispatched";

    /// <summary>Efectivo de una caja del POS depositado en el banco (tesorería, ERP-39).</summary>
    public const string CashDeposited = "cash.deposited";
}

public sealed record QuantityLine(Guid ItemId, decimal Quantity);

// ---------- Publicados por el ERP ----------

public sealed record OrderLine(Guid ItemId, decimal Quantity, decimal UnitPrice);

/// <summary><c>order.created</c>. Extiende v1 con <c>quoteId</c> (si viene de una cotización del CRM).</summary>
public sealed record OrderCreated(
    Guid OrderId, string? Number, Guid CustomerId, Guid? SiteId, IReadOnlyList<OrderLine>? Lines, decimal Total, string? CurrencyCode,
    Guid? QuoteId = null, string? ApprovalStatus = null);

/// <summary>
/// <c>order.approved</c>: el pedido que llegó con <c>approvalStatus = "pending"</c> (superaba el límite de crédito) ya se
/// puede preparar y facturar.
/// </summary>
public sealed record OrderApproved(Guid OrderId, string? Number, string? ApprovedBy);

/// <summary>Línea del comprobante; <c>taxCode</c> es la categoría de impuesto de la base común (extiende v1).</summary>
public sealed record InvoiceLine(Guid? ItemId, string Description, decimal Quantity, decimal UnitPrice, decimal Total, string? TaxCode = null);

/// <summary>
/// <c>invoice.issued</c>. Extiende v1 con <c>kind</c>, <c>subtotal</c>, <c>tax</c>, <c>dueDate</c>, <c>lines</c> y
/// <c>exchangeRate</c> (1 unidad de la moneda del comprobante en la moneda de la empresa, el día de emisión).
/// </summary>
public sealed record InvoiceIssued(
    Guid InvoiceId, string Number, Guid? CustomerId, Guid? SaleId, Guid? OrderId, DateTimeOffset IssuedAt, decimal Total,
    string CurrencyCode, string? Kind = null, decimal Subtotal = 0, decimal Tax = 0, DateOnly? DueDate = null,
    IReadOnlyList<InvoiceLine>? Lines = null, decimal Paid = 0, decimal? ExchangeRate = null, Guid? ShipmentId = null);

/// <summary>
/// <c>payment.received</c>. Extiende v1 con <c>number</c> y <c>reference</c>. <c>amount</c> va siempre en la moneda del
/// comprobante; si se cobró en otra, <c>paidAmount</c>/<c>paidCurrency</c> dicen lo recibido, <c>exchangeRate</c> el tipo del
/// día y <c>fxDifference</c> la diferencia de cambio en moneda de la empresa (positiva si es ganancia).
/// </summary>
public sealed record PaymentReceived(
    Guid PaymentId, Guid? CustomerId, Guid? InvoiceId, Guid? SaleId, decimal Amount, string CurrencyCode, string Method,
    string? Number = null, string? Reference = null, decimal? PaidAmount = null, string? PaidCurrency = null,
    decimal? ExchangeRate = null, decimal? FxDifference = null, Guid? AccountId = null);

/// <summary><c>cash.deposited</c> (POS): efectivo de una caja llevado al banco.</summary>
public sealed record CashDeposited(Guid DepositId, Guid SiteId, decimal Amount, string CurrencyCode, string? Reference = null, string? Bank = null,
    DateOnly? DepositedOn = null, Guid? SessionId = null, string? UserName = null);

/// <summary>
/// <c>credit_note.issued</c> y <c>debit_note.issued</c>: nota vinculada a un comprobante. <c>reason</c> es un motivo común
/// (<c>void</c>, <c>return</c>, <c>discount</c>, <c>correction</c>, <c>interest</c>, <c>increase</c>, <c>penalty</c>); en las
/// líneas devueltas, <c>invoiceLineNo</c> indica la línea del comprobante.
/// </summary>
public sealed record InvoiceNoteIssued(
    Guid NoteId, string Number, Guid InvoiceId, string? InvoiceNumber, Guid? CustomerId, string Reason, DateTimeOffset IssuedAt,
    string CurrencyCode, decimal Subtotal, decimal Tax, decimal Total, IReadOnlyList<NoteLine>? Lines = null, string? Description = null,
    Guid? SaleId = null, Guid? OrderId = null);

public sealed record NoteLine(Guid? ItemId, string Description, decimal Quantity, decimal UnitPrice, decimal Total, string? TaxCode = null,
    int? InvoiceLineNo = null);

/// <summary>
/// <c>invoice.voided</c>: comprobante anulado (sin cobros ni notas). Id del evento determinista por comprobante. Trae
/// <c>customerId</c> para que el CRM lo anote en el cliente y <c>saleId</c> para que el POS libere la venta.
/// </summary>
public sealed record InvoiceVoided(Guid InvoiceId, string? Number, string? Reason, Guid? SaleId = null, Guid? OrderId = null,
    Guid? CustomerId = null);

public sealed record PurchaseOrderLine(Guid ItemId, decimal Quantity, decimal UnitCost);

/// <summary><c>purchase_order.created</c>. Extiende v1 con <c>number</c>, <c>currencyCode</c>, <c>total</c> y <c>expectedAt</c>.</summary>
public sealed record PurchaseOrderCreated(
    Guid PurchaseOrderId, string? Number, Guid SupplierId, Guid SiteId, IReadOnlyList<PurchaseOrderLine>? Lines,
    string? CurrencyCode = null, decimal Total = 0, DateOnly? ExpectedAt = null);

public sealed record PurchaseOrderCancelled(Guid PurchaseOrderId, string? Number, string? Reason);

public static class ErpRecordKinds
{
    public const string PurchaseRequest = "purchase_request";
    public const string SupplierInvoice = "supplier_invoice";
    public const string SupplierPayment = "supplier_payment";
    public const string PurchaseSettings = "purchase_settings";
    public const string TreasuryAccount = "treasury_account";
    public const string TreasuryMovement = "treasury_movement";
    public const string StatementLine = "statement_line";
    public const string LedgerAccount = "ledger_account";
    public const string AccountingSettings = "accounting_settings";
    public const string JournalEntry = "journal_entry";
    public const string CostCenter = "cost_center";
}

/// <summary><c>erp.record.upserted</c>: la versión completa de un registro propio del ERP (gana el más nuevo por <c>UpdatedAt</c>).</summary>
public sealed record ErpRecord(string Kind, string Id, DateTimeOffset UpdatedAt, System.Text.Json.JsonElement Data);

// ---------- Consumidos por el ERP ----------

public sealed record SaleLine(Guid ItemId, string? Sku, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total,
    string? Name = null, bool Taxable = true, string? TaxCode = null);

public sealed record SalePayment(string Method, decimal Amount);

/// <param name="Fiscal">Comprobante electrónico que emitió la caja (POS fase 5a): el ERP no vuelve a facturar la venta.</param>
public sealed record SaleCompleted(
    Guid SaleId, string? Number, Guid SiteId, Guid? CustomerId, string? CurrencyCode, decimal Subtotal, decimal Tax, decimal Total,
    IReadOnlyList<SaleLine>? Lines, IReadOnlyList<SalePayment>? Payments, SaleFiscal? Fiscal = null);

/// <summary>Boleta, factura o nota de crédito electrónica de la caja: tipo, serie y número.</summary>
public sealed record SaleFiscal(string Type, string Series, long Number)
{
    public string FullNumber => $"{Series}-{Number}";
}

public sealed record SaleVoided(Guid SaleId, string? Number, string? Reason);

public sealed record SaleReturned(Guid SaleId, Guid ReturnId, Guid SiteId, decimal Total);

public sealed record QuoteAccepted(
    Guid QuoteId, string? Number, Guid? CustomerId, IReadOnlyList<OrderLine>? Lines, decimal Total, string? CurrencyCode);

public sealed record DealWon(Guid DealId, Guid? CustomerId, decimal Amount, string? CurrencyCode, Guid? QuoteId);

public sealed record GoodsReceived(Guid ReceiptId, string? Number, Guid? PurchaseOrderId, Guid SiteId, IReadOnlyList<QuantityLine>? Lines);

public sealed record StockAdjusted(Guid SiteId, Guid ItemId, decimal QuantityDelta, decimal QuantityOnHand, string? Reason);

public sealed record ShipmentDispatched(
    Guid ShipmentId, Guid OrderId, Guid? CustomerId, string? Carrier, string? TrackingNumber, string? Number,
    IReadOnlyList<QuantityLine>? Lines = null, bool? Partial = null);
