using System.Globalization;
using System.Text;
using VortexErp.Core.Domain;

using static VortexErp.Services.Txt;

namespace VortexErp.Services;

public enum ToastKind
{
    Success,
    Error,
    Info,
}

public sealed record Toast(Guid Id, ToastKind Kind, string Message);

/// <summary>Avisos breves en la esquina inferior derecha.</summary>
public sealed class Toasts
{
    private readonly List<Toast> _items = [];

    public event Action? Changed;
    public IReadOnlyList<Toast> Items => _items;

    public void Success(string message) => Show(ToastKind.Success, message);
    public void Error(string message) => Show(ToastKind.Error, message);
    public void Info(string message) => Show(ToastKind.Info, message);

    public void Dismiss(Guid id)
    {
        _items.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    private void Show(ToastKind kind, string message)
    {
        var toast = new Toast(Guid.NewGuid(), kind, message);
        _items.Add(toast);
        Changed?.Invoke();
        _ = Task.Delay(kind == ToastKind.Error ? 7000 : 4000).ContinueWith(_ => Dismiss(toast.Id), TaskScheduler.Default);
    }
}

/// <summary>Exporta archivos: en escritorio los guarda en Documentos/Vortex ERP y los abre; en móvil, los comparte.</summary>
public sealed class FileExporter
{
    public async Task<string> ExportAsync(string fileName, string content, string contentType = "text/csv")
    {
        var cache = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllTextAsync(cache, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
#if WINDOWS || MACCATALYST
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Vortex ERP");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, fileName);
        File.Copy(cache, target, overwrite: true);
        await Launcher.Default.OpenAsync(new OpenFileRequest(fileName, new ReadOnlyFile(target)));
        return target;
#else
        await Share.Default.RequestAsync(new ShareFileRequest { Title = fileName, File = new ShareFile(cache, contentType) });
        return cache;
#endif
    }
}

/// <summary>Formatos y textos de la interfaz (es-PE).</summary>
public static class Fmt
{
    /// <summary>Cultura de la interfaz: idioma del usuario y país de la empresa (<see cref="SetRegion"/>).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("es-PE");

    public static string Language { get; private set; } = VortexApi.Suite.Regional.Languages.Spanish;

    /// <summary>Zona horaria de la empresa (IANA): las fechas se muestran en su hora, no en la del equipo.</summary>
    public static string? TimeZone { get; private set; }

    public static void SetRegion(string language, string? countryCode, string? timeZone)
    {
        Language = VortexApi.Suite.Regional.Languages.Normalize(language);
        Culture = VortexApi.Suite.Regional.RegionalSettings.CultureFor(Language, countryCode);
        TimeZone = timeZone;
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    public static string Qty(decimal value) => value.ToString(value % 1 == 0 ? "#,0" : "#,0.##", Culture);

    /// <summary>Cifras de las tarjetas: 1,284 · 12.9 mil · 4.2 M.</summary>
    public static string Compact(decimal value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", Culture) + " M",
        >= 10_000 => (value / 1_000).ToString("0.#", Culture) + T("fmt.mil"),
        _ => Qty(Math.Round(value, 2)),
    };

    /// <summary>"1 categoría", "3 categorías".</summary>
    public static string Count(int value, string singular, string plural) => $"{Qty(value)} {(value == 1 ? singular : plural)}";

    public static string Money(decimal value, string currency) => VortexApi.Suite.Money.Currencies.Get(currency).Format(value, Culture);

    public static string MoneyCompact(decimal value, string currency) => $"{Symbol(currency)} {Compact(value)}";

    public static string Symbol(string currency) => VortexApi.Suite.Money.Currencies.Get(currency).Symbol;

    public static DateTimeOffset Local(DateTimeOffset value) =>
        TimeZone is null ? value.ToLocalTime() : VortexApi.Suite.Regional.RegionalSettings.ToCompanyTime(value, TimeZone);

    public static string Date(DateTimeOffset value) => Local(value).ToString("d MMM yyyy", Culture);
    public static string DateTime(DateTimeOffset value) => Local(value).ToString("d MMM yyyy, HH:mm", Culture);

    public static string Ago(DateTimeOffset value)
    {
        var span = DateTimeOffset.Now - value;
        return span.TotalSeconds < 60 ? T("fmt.hace_un_momento")
            : span.TotalMinutes < 60 ? T("fmt.hace_min", (int)span.TotalMinutes)
            : span.TotalHours < 24 ? T("fmt.hace", (int)span.TotalHours)
            : Date(value);
    }

    public static string Initials(string? text)
    {
        var words = (text ?? "?").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}",
        };
    }

    public static string PaymentMethod(string method) => method switch
    {
        PaymentMethods.Cash => T("fmt.efectivo"),
        PaymentMethods.Card => T("fmt.tarjeta"),
        PaymentMethods.Transfer => T("fmt.transferencia"),
        PaymentMethods.Wallet => T("fmt.yape_plin"),
        PaymentMethods.Check => T("fmt.cheque"),
        "credit" => T("fmt.credito"),
        _ => method,
    };

    public static string InvoiceKind(string kind) => kind == InvoiceKinds.Invoice ? T("fmt.factura") : T("fmt.boleta");

    public static string Source(string source) => source switch
    {
        "ERP" => "ERP",
        "CRM" => T("fmt.desde_el_crm"),
        "WMS" => T("fmt.desde_el_wms"),
        _ => source,
    };

    public static string Date(DateOnly value) => value.ToString("dd MMM yyyy", Culture);

    /// <summary>Hoy en Perú (para vencimientos).</summary>
    public static DateOnly Today => VortexApi.Suite.Regional.RegionalSettings.Today(TimeZone ?? "America/Lima");

    public static string TaxCategory(string category) => category switch
    {
        VortexApi.Suite.Taxes.TaxCategories.Standard => T("fmt.tasa_general"),
        VortexApi.Suite.Taxes.TaxCategories.Reduced => T("fmt.tasa_reducida"),
        VortexApi.Suite.Taxes.TaxCategories.SuperReduced => T("fmt.tasa_superreducida"),
        VortexApi.Suite.Taxes.TaxCategories.Increased => T("fmt.tasa_incrementada"),
        VortexApi.Suite.Taxes.TaxCategories.Zero => T("fmt.tasa_cero_exportacion"),
        VortexApi.Suite.Taxes.TaxCategories.Exempt => T("fmt.exonerado_exento"),
        _ => T("fmt.inafecto_no_sujeto"),
    };

    public static string AuditAction(string action) => action switch
    {
        "document.issued" => T("fmt.emision"),
        "payment.registered" => T("fmt.cobro"),
        "document.cancelled" => T("fmt.cancelacion"),
        "document.voided" => T("fmt.anulacion"),
        "price.override" => T("fmt.cambio_de_precio"),
        _ => action,
    };

    public static string SiteKind(string kind) => kind switch
    {
        "Warehouse" => T("fmt.almacen"),
        "Store" => T("fmt.tienda"),
        "Office" => T("fmt.oficina"),
        _ => T("fmt.otro"),
    };
}
