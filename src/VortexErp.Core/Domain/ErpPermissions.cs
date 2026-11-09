using VortexApi.Suite.Security;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Domain;

/// <summary>
/// Qué puede hacer cada rol del ERP. El OrgAdmin asigna el rol en el hub (Sistemas → ERP → miembros); quien no tiene rol
/// entra como <c>consulta</c> (solo ve). Se valida en los servicios de Core, no solo en la interfaz.
/// </summary>
public static class ErpPermissions
{
    public const string CreateOrder = "order.create";
    public const string CancelOrder = "order.cancel";
    public const string ConvertQuote = "quote.convert";
    public const string IssueInvoice = "invoice.issue";
    public const string RegisterPayment = "payment.register";
    public const string CreatePurchase = "purchase.create";
    public const string CancelPurchase = "purchase.cancel";

    /// <summary>Registrar clientes y proveedores.</summary>
    public const string CreateParty = "party.create";

    /// <summary>Crear o editar artículos y sedes (datos maestros de la suite).</summary>
    public const string EditCatalog = "catalog.edit";

    public const string ViewAudit = "audit.view";

    /// <summary>Emitir notas de crédito y débito.</summary>
    public const string IssueNote = "invoice.note";

    /// <summary>Anular un comprobante sin cobros ni notas.</summary>
    public const string VoidInvoice = "invoice.void";

    /// <summary>Fijar el plazo de pago, el límite de crédito y el descuento de un cliente.</summary>
    public const string ManageCredit = "party.credit";

    /// <summary>Aprobar un pedido que supera el límite de crédito del cliente.</summary>
    public const string ApproveCredit = "order.approve_credit";

    /// <summary>Pedir una compra (solicitud).</summary>
    public const string RequestPurchase = "purchase.request";

    /// <summary>Aprobar solicitudes y órdenes sobre el umbral, liberar facturas retenidas y fijar los ajustes de compras.</summary>
    public const string ApprovePurchase = "purchase.approve";

    /// <summary>Registrar facturas de proveedor y sus pagos.</summary>
    public const string PaySuppliers = "supplier.pay";

    /// <summary>Tesorería: cuentas, transferencias, extractos y conciliación.</summary>
    public const string ManageTreasury = "treasury.manage";

    /// <summary>Contabilidad: plan de cuentas, ajustes, asientos manuales y cierre de periodo.</summary>
    public const string ManageAccounting = "accounting.manage";

    /// <summary>Aprobar (o revertir) asientos manuales.</summary>
    public const string ApproveEntries = "accounting.approve";

    public const string ReadOnly = "consulta";
    public const string Billing = "facturador";
    public const string Purchasing = "compras";
    public const string Accountant = "contador";

    /// <summary>Registra asientos manuales, que quedan para que el contador los apruebe.</summary>
    public const string AccountingAssistant = "asistente_contable";

    public static readonly PermissionMatrix Matrix = PermissionMatrix.Create(ReadOnly,
        (ReadOnly, []),
        (Billing, [CreateOrder, CancelOrder, ConvertQuote, IssueInvoice, RegisterPayment, CreateParty, IssueNote, RequestPurchase]),
        (Purchasing, [CreatePurchase, CancelPurchase, CreateParty, RequestPurchase]),
        (Accountant, [IssueInvoice, RegisterPayment, ViewAudit, IssueNote, VoidInvoice, ManageCredit, ApproveCredit, PaySuppliers,
            ApprovePurchase, RequestPurchase, ManageTreasury, ManageAccounting, ApproveEntries]),
        (AccountingAssistant, [ManageAccounting]));

    public static string Describe(string permission) => permission switch
    {
        CreateOrder => T("permiso.createorder"),
        CancelOrder => T("permiso.cancelorder"),
        ConvertQuote => T("permiso.convertquote"),
        IssueInvoice => T("permiso.issueinvoice"),
        RegisterPayment => T("permiso.registerpayment"),
        CreatePurchase => T("permiso.createpurchase"),
        CancelPurchase => T("permiso.cancelpurchase"),
        RequestPurchase => T("permiso.requestpurchase"),
        ApprovePurchase => T("permiso.approvepurchase"),
        PaySuppliers => T("permiso.paysuppliers"),
        ManageTreasury => T("permiso.managetreasury"),
        ManageAccounting => T("permiso.manageaccounting"),
        ApproveEntries => T("permiso.approveentries"),
        CreateParty => T("permiso.createparty"),
        EditCatalog => T("permiso.editcatalog"),
        ViewAudit => T("permiso.viewaudit"),
        IssueNote => T("permiso.issuenote"),
        VoidInvoice => T("permiso.voidinvoice"),
        ManageCredit => T("permiso.managecredit"),
        ApproveCredit => T("permiso.approvecredit"),
        _ => permission,
    };
}
