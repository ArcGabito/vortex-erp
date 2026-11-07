using PdfSharp.Fonts;

namespace VortexErp.Core.Pdf;

/// <summary>
/// Entrega a PDFsharp las fuentes Open Sans incrustadas en el ensamblado, para que el PDF salga igual en Windows,
/// Android, iOS y macOS (que no tienen las mismas fuentes instaladas).
/// </summary>
internal sealed class PdfFonts : IFontResolver
{
    public const string Family = "Open Sans";

    private const string Regular = "OpenSans-Regular";
    private const string Bold = "OpenSans-Bold";

    private static readonly Lock Gate = new();

    /// <summary>Registra el resolvedor una sola vez; PDFsharp no permite cambiarlo después de usarlo.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            GlobalFontSettings.FontResolver ??= new PdfFonts();
        }
    }

    // Cualquier familia que se pida se resuelve con Open Sans.
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? Bold : Regular, mustSimulateBold: false, mustSimulateItalic: isItalic);

    public byte[]? GetFont(string faceName)
    {
        using var resource = typeof(PdfFonts).Assembly.GetManifestResourceStream($"VortexErp.Fonts.{faceName}.ttf")
            ?? throw new InvalidOperationException($"No se encontró la fuente incrustada {faceName}.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        return memory.ToArray();
    }
}
