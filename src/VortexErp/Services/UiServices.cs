using System.Globalization;
using System.Text;
using VortexErp.Core.Domain;

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
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-PE");

    public static string Qty(decimal value) => value.ToString(value % 1 == 0 ? "#,0" : "#,0.##", Culture);

    /// <summary>Cifras de las tarjetas: 1,284 · 12.9 mil · 4.2 M.</summary>
    public static string Compact(decimal value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", Culture) + " M",
        >= 10_000 => (value / 1_000).ToString("0.#", Culture) + " mil",
        _ => Qty(Math.Round(value, 2)),
    };

    /// <summary>"1 categoría", "3 categorías".</summary>
    public static string Count(int value, string singular, string plural) => $"{Qty(value)} {(value == 1 ? singular : plural)}";

    public static string Money(decimal value, string currency) => $"{Symbol(currency)} {value.ToString("#,0.00", Culture)}";

    public static string MoneyCompact(decimal value, string currency) => $"{Symbol(currency)} {Compact(value)}";

    public static string Symbol(string currency) => currency switch
    {
        "PEN" => "S/",
        "USD" => "US$",
        "EUR" => "€",
        "MXN" or "COP" or "CLP" or "ARS" => "$",
        "BOB" => "Bs",
        _ => currency,
    };

    public static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("dd MMM yyyy", Culture);
    public static string DateTime(DateTimeOffset value) => value.ToLocalTime().ToString("dd MMM yyyy, HH:mm", Culture);

    public static string Ago(DateTimeOffset value)
    {
        var span = DateTimeOffset.Now - value;
        return span.TotalSeconds < 60 ? "hace un momento"
            : span.TotalMinutes < 60 ? $"hace {(int)span.TotalMinutes} min"
            : span.TotalHours < 24 ? $"hace {(int)span.TotalHours} h"
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
        PaymentMethods.Cash => "Efectivo",
        PaymentMethods.Card => "Tarjeta",
        PaymentMethods.Transfer => "Transferencia",
        PaymentMethods.Wallet => "Yape / Plin",
        PaymentMethods.Check => "Cheque",
        "credit" => "Crédito",
        _ => method,
    };

    public static string InvoiceKind(string kind) => kind == InvoiceKinds.Invoice ? "Factura" : "Boleta";

    public static string Source(string source) => source switch
    {
        "ERP" => "ERP",
        "CRM" => "Desde el CRM",
        "WMS" => "Desde el WMS",
        _ => source,
    };

    public static string Date(DateOnly value) => value.ToString("dd MMM yyyy", Culture);

    /// <summary>Hoy en Perú (para vencimientos).</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5)).DateTime);

    public static string SiteKind(string kind) => kind switch
    {
        "Warehouse" => "Almacén",
        "Store" => "Tienda",
        "Office" => "Oficina",
        _ => "Otro",
    };
}
