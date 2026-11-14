using VortexApi.Client;

namespace VortexErp.Core.Domain;

/// <summary>Estado del comprobante electrónico ante la autoridad tributaria (fase 7a).</summary>
public static class FiscalStatuses
{
    /// <summary>Emitido en el ERP, sin respuesta todavía (sin red queda así y se envía al reconectar).</summary>
    public const string Pending = "pending";
    public const string Accepted = "accepted";

    /// <summary>Aceptado con observaciones: es válido.</summary>
    public const string Observed = "observed";
    public const string Rejected = "rejected";

    /// <summary>El hub no lo pudo enviar (clave SOL, certificado, SUNAT caída): se reintenta.</summary>
    public const string Failed = "failed";

    /// <summary>El hub no lo aceptó (datos inválidos o número repetido): hay que revisarlo.</summary>
    public const string Error = "error";

    /// <summary>Anulado ante SUNAT (comunicación de baja o resumen diario aceptados).</summary>
    public const string Voided = "voided";

    public static string From(VortexFiscalStatus status) => status switch
    {
        VortexFiscalStatus.Accepted => Accepted,
        VortexFiscalStatus.Observed => Observed,
        VortexFiscalStatus.Rejected => Rejected,
        VortexFiscalStatus.Failed => Failed,
        VortexFiscalStatus.Voided => Voided,
        _ => Pending,
    };

    public static bool IsValid(string? status) => status is Accepted or Observed;
}

/// <summary>Si este equipo emite comprobantes electrónicos y, si no, por qué (para mostrarlo en Configuración).</summary>
public sealed record FiscalSetup(bool Enabled, string? Reason);

/// <summary>El comprobante electrónico de un comprobante o nota del ERP (de este equipo o de otro).</summary>
public sealed record FiscalDocumentRow(
    Guid Id, string Kind, string Series, long Number, Guid? InvoiceId, string Status, string? Hash, string? Qr, string? ResponseCode,
    string? ResponseDescription, string? Notes, string? LastError, bool Local, bool Sent, DateTimeOffset CreatedAt, string? VoidState = null,
    string? VoidMessage = null)
{
    public string FullNumber => $"{Series}-{Number:D6}";
}

/// <summary>Cómo habla el ERP con el hub para los comprobantes (en las pruebas, uno falso).</summary>
public interface IFiscalGateway
{
    Task<VortexFiscalDocument> IssueAsync(VortexFiscalRequest request, CancellationToken ct = default);
    Task<VortexFiscalDocument> RetryAsync(Guid hubId, CancellationToken ct = default);

    /// <summary>Pide al hub la baja (comunicación de baja o resumen diario) del comprobante con ese Id del ERP.</summary>
    Task<VortexFiscalVoid> VoidAsync(Guid sourceId, string reason, CancellationToken ct = default);
}

/// <summary>En qué va la anulación electrónica de un comprobante (fase 7b).</summary>
public static class FiscalVoidStates
{
    /// <summary>Pedida en este equipo; se envía al hub al sincronizar.</summary>
    public const string Requested = "requested";

    /// <summary>El hub la recibió y la envió a SUNAT; se espera la respuesta.</summary>
    public const string Sent = "sent";

    public const string Accepted = "accepted";

    /// <summary>SUNAT la rechazó o el hub no la aceptó (fuera de plazo): el comprobante sigue vigente.</summary>
    public const string Rejected = "rejected";
}
