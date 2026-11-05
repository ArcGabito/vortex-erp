using VortexApi.Suite.Text;

namespace VortexErp.Services;

/// <summary>
/// Textos de la interfaz en el idioma elegido (español, inglés o portugués). Los textos de la app están en
/// <c>VortexErp.Core/Texts/{es,en,pt}.json</c> y los comunes de la suite en <c>VortexApi.Suite</c>. Se usa como
/// <c>@T(clave)</c> en Razor y <c>T(clave, args…)</c> en C#.
/// </summary>
public static class Txt
{
    public static readonly TextCatalog Catalog =
        new TextCatalog().AddResources(typeof(VortexErp.Core.ErpWorkspace).Assembly, "VortexErp.Core.Texts");

    public static string T(string key, params object?[] args) => Catalog.Get(Fmt.Language, key, args);
}
