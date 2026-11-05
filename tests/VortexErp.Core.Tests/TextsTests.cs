using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using VortexApi.Suite.Regional;
using VortexApi.Suite.Text;

namespace VortexErp.Core.Tests;

/// <summary>Los textos de la interfaz están completos en español, inglés y portugués.</summary>
public sealed partial class TextsTests
{
    private static readonly Assembly Core = typeof(ErpWorkspace).Assembly;

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = Core.GetManifestResourceStream($"VortexErp.Core.Texts.{language}.json")
            ?? throw new InvalidOperationException($"Falta Texts/{language}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    [Theory]
    [InlineData(Languages.English)]
    [InlineData(Languages.Portuguese)]
    public void Cada_texto_existe_en_cada_idioma_con_los_mismos_marcadores(string language)
    {
        var spanish = Load(Languages.Spanish);
        var other = Load(language);

        Assert.Empty(spanish.Keys.Except(other.Keys));
        Assert.Empty(other.Keys.Except(spanish.Keys));
        var mismatched = spanish.Where(e => Markers(e.Value) != Markers(other[e.Key])).Select(e => e.Key);
        Assert.Empty(mismatched);
    }

    [Fact]
    public void Cada_clave_usada_en_la_interfaz_existe()
    {
        var catalog = new TextCatalog().AddResources(Core, "VortexErp.Core.Texts");
        var app = Path.Combine(RepoRoot(), "src", "VortexErp");
        var used = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".razor") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => KeyUse().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(k => !k.EndsWith('.'))
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);
        var missing = used.Where(k => catalog.Get(Languages.Spanish, k) == k).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void El_idioma_se_elige_y_si_falta_una_clave_se_usa_el_espanol()
    {
        var catalog = new TextCatalog().AddResources(Core, "VortexErp.Core.Texts");
        var key = Load(Languages.Spanish).Keys.First(k => k.StartsWith("settings."));
        Assert.NotEqual(catalog.Get(Languages.Spanish, key), catalog.Get(Languages.English, key));
        Assert.Equal(catalog.Get(Languages.Spanish, key), catalog.Get("fr", key));
        Assert.Equal(Languages.Portuguese, Languages.DefaultFor("BR"));
        Assert.Equal(Languages.English, Languages.DefaultFor("US"));
        Assert.Equal(Languages.Spanish, Languages.DefaultFor("PE"));
    }

    private static string Markers(string text) =>
        string.Join(",", Regex.Matches(text, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).Order());

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VortexErp.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }

    [GeneratedRegex("""\bT\("([a-z0-9_.]+)"[,)]""")]
    private static partial Regex KeyUse();
}
