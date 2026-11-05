using VortexApi.Suite.Regional;
using VortexApi.Suite.Text;

namespace VortexErp.Core.Texts;

/// <summary>
/// Textos de la app en el idioma de la interfaz (<c>Texts/{es,en,pt}.json</c>): los mensajes de validación de Core y
/// los textos de la UI usan el mismo catálogo. La app fija <see cref="Language"/> al elegir idioma; las pruebas pueden
/// cambiarlo solo para su flujo con <see cref="Use"/>.
/// </summary>
public static class CoreText
{
    private static readonly AsyncLocal<string?> Scoped = new();
    private static string _language = Languages.Spanish;

    public static readonly TextCatalog Catalog = new TextCatalog().AddResources(typeof(CoreText).Assembly, "VortexErp.Core.Texts");

    public static string Language
    {
        get => Scoped.Value ?? _language;
        set => _language = Languages.Normalize(value);
    }

    public static string T(string key, params object?[] args) => Catalog.Get(Language, key, args);

    /// <summary>Usa otro idioma hasta el <c>Dispose</c>, solo en el flujo actual (pruebas).</summary>
    public static IDisposable Use(string language)
    {
        var previous = Scoped.Value;
        Scoped.Value = Languages.Normalize(language);
        return new Restore(() => Scoped.Value = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
