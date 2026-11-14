using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Suite.Taxes;
using VortexErp.Core.Data;
using VortexErp.Core.Domain;
using VortexErp.Core.Integration;

using static VortexErp.Core.Texts.CoreText;

namespace VortexErp.Core.Services;

/// <summary>
/// Comprobante electrónico (fase 7a, ERP-75) con el paquete de localización del hub: con el módulo
/// <c>facturacion_electronica</c> y una empresa peruana con RUC, cada factura, boleta y nota que emite el ERP se numera
/// aquí (en la misma transacción, con la serie F o B + equipo que ya usa el ERP, así dos equipos sin conexión no repiten),
/// queda en cola y se envía al hub al sincronizar; el hub la firma, la envía a SUNAT y avisa con
/// <c>hub.fiscal.accepted|rejected</c> a todos los equipos.
/// </summary>
public sealed class FiscalService(ErpDatabase db, IErpContext context, TimeProvider clock) : ErpService(db, context, clock)
{
    /// <summary>Módulo del hub que habilita la emisión (<c>features</c> del acceso de la organización al ERP).</summary>
    public const string Feature = "facturacion_electronica";

    public const string ProductCode = "ERP";

    public static readonly string[] Countries = ["PE"];

    /// <summary>Monto desde el que una boleta identifica al cliente (S/ 700).</summary>
    public const decimal ReceiptIdentifyFrom = 700m;

    // ---------- Configuración ----------

    public async Task<FiscalSetup> GetSetupAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await SetupAsync(c, null);
    }

    internal async Task<FiscalSetup> SetupAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        if (!Context.Features.Contains(Feature, StringComparer.OrdinalIgnoreCase))
            return new FiscalSetup(false, T("fiscal.sin_modulo"));
        var company = await c.FirstOrDefaultAsync("SELECT country_code, tax_id FROM company WHERE id = 1",
            r => new { Country = r.StrOrNull("country_code"), TaxId = r.StrOrNull("tax_id") }, tx);
        if (company is null || !Countries.Contains(company.Country))
            return new FiscalSetup(false, T("fiscal.sin_pais"));
        if (!IsValidRuc(company.TaxId))
            return new FiscalSetup(false, T("fiscal.sin_ruc"));
        return new FiscalSetup(true, null);
    }

    // ---------- Emisión (en la transacción del comprobante o la nota) ----------

    /// <summary>
    /// Arma el comprobante electrónico de una factura o boleta recién emitida. Sin emisión configurada no hace nada (el ERP
    /// factura como antes). Las reglas de SUNAT que conviene ver antes de numerar se validan aquí.
    /// </summary>
    internal async Task IssueForInvoiceAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, InvoiceIssued e, string kind)
    {
        if (!(await SetupAsync(c, tx)).Enabled)
            return;
        var customer = await CustomerAsync(c, tx, e.CustomerId);
        var type = kind == InvoiceKinds.Invoice ? VortexFiscalTypes.Invoice : VortexFiscalTypes.Receipt;
        if (type == VortexFiscalTypes.Invoice)
            Require(customer.DocType == "6" && IsValidRuc(customer.DocNumber), T("fiscal.factura_sin_ruc"));
        else if (customer.DocType == "6" && !IsValidRuc(customer.DocNumber))
            throw new ErpValidationException(T("fiscal.ruc_invalido"));
        if (type == VortexFiscalTypes.Receipt && customer.DocNumber is null && e.CurrencyCode == "PEN" && e.Total >= ReceiptIdentifyFrom)
            throw new ErpValidationException(T("fiscal.boleta_sin_documento", ReceiptIdentifyFrom));

        var (series, number) = Split(e.Number);
        var regime = await TaxRegimeAsync(c, tx);
        var lines = new List<VortexFiscalLine>();
        foreach (var l in e.Lines ?? [])
            lines.Add(await LineAsync(c, tx, regime, l.ItemId, l.Description, l.Quantity, l.Total, l.TaxCode));

        var local = now.ToOffset(ErpClock.Offset);
        var issued = DateOnly.FromDateTime(local.DateTime);
        // Factura al crédito: SUNAT pide la forma de pago con sus cuotas (una, al vencimiento, por lo que queda por cobrar).
        VortexFiscalPayment? payment = null;
        var due = e.Total - e.Paid;
        if (type == VortexFiscalTypes.Invoice && due > 0 && e.DueDate is { } dueDate && dueDate > issued)
            payment = new VortexFiscalPayment(true, [new VortexFiscalInstallment(due, dueDate)]);
        var request = new VortexFiscalRequest(e.InvoiceId, type, series, number, issued, e.CurrencyCode, customer, lines,
            TimeOnly.FromDateTime(local.DateTime), payment is null ? null : e.DueDate, payment);
        await InsertLocalAsync(c, tx, now, request, e.InvoiceId);
    }

    /// <summary>
    /// Nota de crédito o débito electrónica de un comprobante que también lo fue (con su referencia y el motivo del catálogo
    /// 9 o 10 de SUNAT). Si el comprobante se emitió antes de activar el módulo, la nota tampoco es electrónica.
    /// </summary>
    internal async Task IssueForNoteAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, InvoiceNoteIssued e, string noteKind)
    {
        var original = await c.FirstOrDefaultAsync("""
            SELECT kind, series, number FROM fiscal_documents WHERE id = $id AND kind IN ('invoice', 'receipt')
            """, r => new { Kind = r.Str("kind"), Series = r.Str("series"), Number = r.Long("number") }, tx, ("$id", e.InvoiceId));
        if (original is null)
            return;
        var setup = await SetupAsync(c, tx);
        Require(setup.Enabled, T("fiscal.nota_sin_configuracion", setup.Reason ?? ""));

        var (series, number) = Split(e.Number);
        var regime = await TaxRegimeAsync(c, tx);
        var lines = new List<VortexFiscalLine>();
        foreach (var l in (e.Lines ?? []).Where(l => l.Total > 0))
            lines.Add(await LineAsync(c, tx, regime, l.ItemId, l.Description, l.Quantity, l.Total, l.TaxCode));
        var local = now.ToOffset(ErpClock.Offset);
        var description = Truncate(Clean(e.Description) ?? T("fiscal.motivo." + e.Reason), 250);
        var request = new VortexFiscalRequest(e.NoteId, noteKind == NoteKinds.Credit ? VortexFiscalTypes.CreditNote : VortexFiscalTypes.DebitNote,
            series, number, DateOnly.FromDateTime(local.DateTime), e.CurrencyCode, await CustomerAsync(c, tx, e.CustomerId), lines,
            TimeOnly.FromDateTime(local.DateTime),
            Reference: new VortexFiscalReference(original.Kind, original.Series, original.Number, ReasonCode(e.Reason), description));
        await InsertLocalAsync(c, tx, now, request, e.InvoiceId);
    }

    /// <summary>
    /// Un comprobante electrónico no se anula borrándolo: SUNAT ya lo tiene (o lo tendrá al sincronizar). Se corrige con una
    /// nota de crédito por anulación (motivo 01).
    /// </summary>
    internal static async Task RequireNotElectronicAsync(SqliteConnection c, SqliteTransaction tx, Guid invoiceId) =>
        Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM fiscal_documents WHERE id = $id", tx, ("$id", invoiceId)) == 0,
            T("fiscal.anular_con_nota"));

    /// <summary>Motivo del ERP al código de SUNAT: catálogo 9 (notas de crédito) y 10 (notas de débito).</summary>
    public static string ReasonCode(string reason) => reason switch
    {
        NoteReasons.Void => "01",
        NoteReasons.Correction => "03",
        NoteReasons.Return => "07",
        NoteReasons.Discount => "09",
        NoteReasons.Interest => "01",
        NoteReasons.Increase => "02",
        NoteReasons.Penalty => "03",
        _ => "10",
    };

    private static async Task<VortexFiscalLine> LineAsync(SqliteConnection c, SqliteTransaction tx, TaxRegime regime, Guid? itemId, string name,
        decimal quantity, decimal total, string? taxCode)
    {
        var item = itemId is { } id
            ? await c.FirstOrDefaultAsync("SELECT sku, kind, unit_code FROM items WHERE id = $id",
                r => new { Sku = r.Str("sku"), Kind = r.Str("kind"), UnitCode = r.StrOrNull("unit_code") }, tx, ("$id", id))
            : null;
        var unit = item?.UnitCode is { Length: >= 2 and <= 3 } code ? code.ToUpperInvariant() : item is null || item.Kind == "Service" ? "ZZ" : "NIU";
        var (category, rate) = Category(regime, taxCode);
        return new VortexFiscalLine(Truncate(name, 500), quantity <= 0 ? 1 : quantity, unit, 0, category, rate,
            item?.Sku is { Length: <= 30 } sku ? sku : null, total);
    }

    /// <summary>Categoría fiscal de la categoría de impuesto de la base común: con tasa, gravado; exonerado; lo demás, inafecto.</summary>
    public static (string Category, decimal Rate) Category(TaxRegime regime, string? taxCode)
    {
        var rate = regime.RateOf(taxCode);
        if (rate > 0)
            return (VortexFiscalTaxCategories.Taxed, rate);
        return TaxCategories.Normalize(taxCode) == TaxCategories.Exempt
            ? (VortexFiscalTaxCategories.Exempt, 0)
            : (VortexFiscalTaxCategories.Unaffected, 0);
    }

    /// <summary>Cliente del comprobante (catálogo 6): sin cliente o sin documento, "clientes varios".</summary>
    private static async Task<VortexFiscalParty> CustomerAsync(SqliteConnection c, SqliteTransaction tx, Guid? customerId)
    {
        var party = customerId is { } id
            ? await c.FirstOrDefaultAsync("""
                SELECT name, document_type, document_number, json_extract(hub_data, '$.address.line') AS address_line, city FROM parties WHERE id = $id
                """,
                r => new { Name = r.Str("name"), Type = r.StrOrNull("document_type"), Number = r.StrOrNull("document_number"),
                    Address = string.Join(", ", new[] { r.StrOrNull("address_line"), r.StrOrNull("city") }.Where(x => !string.IsNullOrWhiteSpace(x))) },
                tx, ("$id", id))
            : null;
        if (party is null || party.Number is null)
            return new VortexFiscalParty("-", null, party?.Name ?? T("fiscal.clientes_varios"));
        var docType = party.Type?.ToUpperInvariant() switch
        {
            "RUC" => "6",
            "DNI" => "1",
            "CE" => "4",
            "PASAPORTE" or "PASSPORT" => "7",
            _ => "0",
        };
        return new VortexFiscalParty(docType, party.Number, Truncate(party.Name, 300), party.Address.Length > 0 ? Truncate(party.Address, 200) : null);
    }

    /// <summary>"FEQP-000012" → serie FEQP, número 12.</summary>
    internal static (string Series, long Number) Split(string number)
    {
        var dash = number.LastIndexOf('-');
        return (number[..dash].ToUpperInvariant(), long.Parse(number[(dash + 1)..], CultureInfo.InvariantCulture));
    }

    private static Task InsertLocalAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, VortexFiscalRequest request, Guid invoiceId) =>
        c.ExecAsync("""
            INSERT INTO fiscal_documents (id, kind, series, number, invoice_id, request, status, created_at, updated_at)
            VALUES ($id, $kind, $series, $number, $invoice, $request, 'pending', $now, $now)
            """, tx, ("$id", request.SourceId), ("$kind", request.Type), ("$series", request.Series), ("$number", request.Number),
            ("$invoice", invoiceId), ("$request", JsonSerializer.Serialize(request, VortexJson.Options)), ("$now", now));

    /// <summary>
    /// Respuesta de la autoridad (<c>hub.fiscal.*</c>): la reciben todos los equipos. El de otro equipo se anota aquí (el
    /// comprobante o la nota llegan por su propio evento).
    /// </summary>
    internal static async Task<int> ApplyResponseAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, VortexFiscalResponded r)
    {
        var invoiceId = await c.ScalarAsync<string?>("SELECT invoice_id FROM invoice_notes WHERE id = $id", tx, ("$id", r.SourceId))
            ?? r.SourceId.ToString();
        return await c.ExecAsync("""
            INSERT INTO fiscal_documents (id, kind, series, number, invoice_id, sent, status, hash, qr, response_code, response_description, notes,
                                          created_at, updated_at)
            VALUES ($id, $kind, $series, $number, $invoice, 1, $status, $hash, $qr, $code, $desc, $notes, $at, $at)
            ON CONFLICT (id) DO UPDATE SET status = $status, hash = COALESCE($hash, hash), qr = COALESCE($qr, qr), response_code = $code,
                response_description = $desc, notes = $notes, sent = 1, last_error = NULL, updated_at = $at
            """, tx, ("$id", r.SourceId), ("$kind", r.DocumentType), ("$series", r.Series.ToUpperInvariant()), ("$number", r.Number),
            ("$invoice", invoiceId), ("$status", FiscalStatuses.From(r.Status)), ("$hash", r.Hash), ("$qr", r.QrText), ("$code", r.ResponseCode),
            ("$desc", r.ResponseDescription), ("$notes", r.Notes), ("$at", at));
    }

    // ---------- Envío al hub ----------

    /// <summary>
    /// Pide al hub los comprobantes de este equipo que aún no recibió. Se corta al primer error de conexión; uno que el hub
    /// no acepta (datos, número repetido) queda en <see cref="FiscalStatuses.Error"/>. Devuelve cuántos se enviaron.
    /// </summary>
    public async Task<int> SendPendingAsync(IFiscalGateway gateway, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var pending = await c.QueryAsync("""
            SELECT id, request FROM fiscal_documents WHERE request IS NOT NULL AND sent = 0 AND status = 'pending' ORDER BY created_at, number
            """, r => (Id: r.Guid("id"), Request: r.Str("request")));
        var sent = 0;
        foreach (var (id, json) in pending)
        {
            var request = JsonSerializer.Deserialize<VortexFiscalRequest>(json, VortexJson.Options)!;
            var now = Clock.GetUtcNow();
            try
            {
                var document = await gateway.IssueAsync(request, ct);
                await c.ExecAsync("""
                    UPDATE fiscal_documents SET sent = 1, hub_id = $hub, status = $status, hash = $hash, qr = $qr, response_code = $code,
                        response_description = $desc, notes = $notes, last_error = NULL, updated_at = $now
                    WHERE id = $id
                    """, null, ("$id", id), ("$hub", document.Id), ("$status", FiscalStatuses.From(document.Status)), ("$hash", document.Hash),
                    ("$qr", document.QrText), ("$code", document.ResponseCode), ("$desc", document.ResponseDescription), ("$notes", document.Notes),
                    ("$now", now));
                sent++;
            }
            catch (VortexApiException ex) when (ex.Code is "fiscal_not_enabled" or "fiscal_not_configured" or "fiscal_country_unsupported")
            {
                // El hub aún no está listo (sin perfil fiscal o sin el módulo): todos fallarían igual; se reintenta luego.
                await c.ExecAsync("UPDATE fiscal_documents SET last_error = $error, updated_at = $now WHERE id = $id", null,
                    ("$id", id), ("$error", Truncate(ex.Message, 500)), ("$now", now));
                break;
            }
            catch (VortexApiException ex) when (ex.StatusCode is 400 or 409 or 422)
            {
                await c.ExecAsync("UPDATE fiscal_documents SET status = 'error', last_error = $error, updated_at = $now WHERE id = $id", null,
                    ("$id", id), ("$error", Truncate(ex.Message, 500)), ("$now", now));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or VortexApiException && !ct.IsCancellationRequested)
            {
                break;
            }
        }
        return sent;
    }

    /// <summary>Vuelve a enviar un comprobante que falló en el hub o que el hub no aceptó.</summary>
    public async Task RetryAsync(IFiscalGateway gateway, Guid documentId, CancellationToken ct = default)
    {
        RequirePermission(ErpPermissions.IssueInvoice);
        await using var c = await Db.OpenAsync(ct);
        var doc = await c.FirstOrDefaultAsync("SELECT status, hub_id, request FROM fiscal_documents WHERE id = $id",
            r => new { Status = r.Str("status"), Hub = r.GuidOrNull("hub_id"), Local = r.StrOrNull("request") is not null }, null, ("$id", documentId));
        Require(doc is not null, T("fiscal.no_existe"));
        Require(doc!.Status is FiscalStatuses.Failed or FiscalStatuses.Error, T("fiscal.no_reintentable"));
        var now = Clock.GetUtcNow();
        if (doc.Status == FiscalStatuses.Failed && doc.Hub is { } hubId)
        {
            var document = await gateway.RetryAsync(hubId, ct);
            await c.ExecAsync("UPDATE fiscal_documents SET status = $status, last_error = NULL, updated_at = $now WHERE id = $id", null,
                ("$id", documentId), ("$status", FiscalStatuses.From(document.Status)), ("$now", now));
        }
        else
        {
            Require(doc.Local, T("fiscal.de_otro_equipo"));
            await c.ExecAsync("UPDATE fiscal_documents SET status = 'pending', sent = 0, last_error = NULL, updated_at = $now WHERE id = $id", null,
                ("$id", documentId), ("$now", now));
            await SendPendingAsync(gateway, ct);
        }
        await using var tx = c.BeginTransaction();
        await AuditAsync(c, tx, now, "fiscal.retried", "fiscal", documentId, null, null);
        await tx.CommitAsync(ct);
    }

    // ---------- Consultas ----------

    /// <summary>El comprobante electrónico de un comprobante y los de sus notas.</summary>
    public async Task<IReadOnlyList<FiscalDocumentRow>> GetForInvoiceAsync(Guid invoiceId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("SELECT * FROM fiscal_documents WHERE invoice_id = $id ORDER BY created_at", Read, null, ("$id", invoiceId));
    }

    /// <summary>Estado ante SUNAT de cada comprobante de la lista (solo los electrónicos).</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetStatusesAsync(IEnumerable<Guid> invoiceIds, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return (await c.QueryAsync("SELECT id, status FROM fiscal_documents WHERE id IN (SELECT value FROM json_each($ids))",
                r => (Id: r.Guid("id"), Status: r.Str("status")), null, ("$ids", JsonSerializer.Serialize(invoiceIds.Select(i => i.ToString())))))
            .ToDictionary(x => x.Id, x => x.Status);
    }

    /// <summary>Los que no quedaron válidos (por enviar, rechazados, con error): para revisarlos en Comprobantes.</summary>
    public async Task<IReadOnlyList<FiscalDocumentRow>> GetOutstandingAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT * FROM fiscal_documents WHERE status NOT IN ('accepted', 'observed') ORDER BY created_at DESC LIMIT 200
            """, Read);
    }

    private static FiscalDocumentRow Read(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("kind"), r.Str("series"), r.Long("number"), r.GuidOrNull("invoice_id"), r.Str("status"), r.StrOrNull("hash"),
        r.StrOrNull("qr"), r.StrOrNull("response_code"), r.StrOrNull("response_description"), r.StrOrNull("notes"), r.StrOrNull("last_error"),
        r.StrOrNull("request") is not null, r.Bool("sent"), r.Time("created_at"));

    /// <summary>Código de tipo de comprobante de SUNAT (catálogo 1).</summary>
    public static string TypeCode(string kind) => kind switch
    {
        VortexFiscalTypes.Invoice => "01",
        VortexFiscalTypes.Receipt => "03",
        VortexFiscalTypes.CreditNote => "07",
        VortexFiscalTypes.DebitNote => "08",
        _ => "00",
    };

    /// <summary>Texto del QR de la representación impresa (si el hub aún no lo devolvió): RUC|tipo|serie|número|IGV|total|fecha|tipo doc|doc|resumen|.</summary>
    public static string QrText(string ruc, string kind, string series, long number, decimal tax, decimal total, DateOnly issued, string? customerDoc,
        string? hash)
    {
        var docType = customerDoc switch { { Length: 11 } => "6", { Length: 8 } => "1", null or "" => "-", _ => "0" };
        return string.Join('|', ruc, TypeCode(kind), series, number, tax.ToString("0.00", CultureInfo.InvariantCulture),
            total.ToString("0.00", CultureInfo.InvariantCulture), issued.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), docType,
            string.IsNullOrEmpty(customerDoc) ? "-" : customerDoc, hash ?? "") + "|";
    }

    /// <summary>La representación impresa del comprobante en PDF (con QR y resumen si es electrónico).</summary>
    public async Task<byte[]> InvoicePdfAsync(Guid invoiceId, CultureInfo culture, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var invoice = await c.FirstOrDefaultAsync("""
            SELECT i.*, p.name AS customer_name, p.document_number AS customer_document FROM invoices i LEFT JOIN parties p ON p.id = i.customer_id
            WHERE i.id = $id
            """, InvoiceService.Read, null, ("$id", invoiceId));
        Require(invoice is not null, T("error.el_comprobante_no_existe"));
        var lines = await InvoiceService.LinesAsync(c, null, invoiceId);
        var company = await c.FirstOrDefaultAsync("SELECT * FROM company WHERE id = 1", r => new CompanyInfo(r.Str("legal_name"), r.StrOrNull("trade_name"),
            r.StrOrNull("tax_id"), r.Str("country_code"), r.Str("currency_code"), r.DecOrNull("default_tax_rate"), r.StrOrNull("time_zone")));
        var fiscal = await c.FirstOrDefaultAsync("SELECT * FROM fiscal_documents WHERE id = $id", Read, null, ("$id", invoiceId));
        return Pdf.InvoicePdf.Generate(invoice!, lines, company, await TaxRegimeAsync(c, null), fiscal, culture);
    }

    /// <summary>RUC de 11 dígitos con su dígito verificador (módulo 11).</summary>
    public static bool IsValidRuc(string? ruc)
    {
        if (ruc is not { Length: 11 } || !ruc.All(char.IsAsciiDigit) || ruc[..2] is not ("10" or "15" or "16" or "17" or "20"))
            return false;
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var sum = weights.Select((w, i) => w * (ruc[i] - '0')).Sum();
        var check = 11 - sum % 11;
        return (check == 10 ? 0 : check == 11 ? 1 : check) == ruc[10] - '0';
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
