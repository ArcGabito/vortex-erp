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

    public static string From(VortexFiscalStatus status) => status switch
    {
        VortexFiscalStatus.Accepted => Accepted,
        VortexFiscalStatus.Observed => Observed,
        VortexFiscalStatus.Rejected => Rejected,
        VortexFiscalStatus.Failed => Failed,
        _ => Pending,
    };

    public static bool IsValid(string? status) => status is Accepted or Observed;
}

/// <summary>Si este equipo emite comprobantes electrónicos y, si no, por qué (para mostrarlo en Configuración).</summary>
public sealed record FiscalSetup(bool Enabled, string? Reason);

/// <summary>El comprobante electrónico de un comprobante o nota del ERP (de este equipo o de otro).</summary>
public sealed record FiscalDocumentRow(
    Guid Id, string Kind, string Series, long Number, Guid? InvoiceId, string Status, string? Hash, string? Qr, string? ResponseCode,
    string? ResponseDescription, string? Notes, string? LastError, bool Local, bool Sent, DateTimeOffset CreatedAt)
{
    public string FullNumber => $"{Series}-{Number:D6}";
}

/// <summary>Cómo habla el ERP con el hub para los comprobantes (en las pruebas, uno falso).</summary>
public interface IFiscalGateway
{
    Task<VortexFiscalDocument> IssueAsync(VortexFiscalRequest request, CancellationToken ct = default);
    Task<VortexFiscalDocument> RetryAsync(Guid hubId, CancellationToken ct = default);
}
