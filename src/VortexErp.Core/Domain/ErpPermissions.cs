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

    public const string ReadOnly = "consulta";
    public const string Billing = "facturador";
    public const string Purchasing = "compras";
    public const string Accountant = "contador";

    public static readonly PermissionMatrix Matrix = PermissionMatrix.Create(ReadOnly,
        (ReadOnly, []),
        (Billing, [CreateOrder, CancelOrder, ConvertQuote, IssueInvoice, RegisterPayment, CreateParty]),
        (Purchasing, [CreatePurchase, CancelPurchase, CreateParty]),
        (Accountant, [IssueInvoice, RegisterPayment, ViewAudit]));

    public static string Describe(string permission) => permission switch
    {
        CreateOrder => T("permiso.createorder"),
        CancelOrder => T("permiso.cancelorder"),
        ConvertQuote => T("permiso.convertquote"),
        IssueInvoice => T("permiso.issueinvoice"),
        RegisterPayment => T("permiso.registerpayment"),
        CreatePurchase => T("permiso.createpurchase"),
        CancelPurchase => T("permiso.cancelpurchase"),
        CreateParty => T("permiso.createparty"),
        EditCatalog => T("permiso.editcatalog"),
        ViewAudit => T("permiso.viewaudit"),
        _ => permission,
    };
}
