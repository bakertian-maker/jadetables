using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GameWeb.Games.Mahjong;
using GameWeb.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameWeb.Tests;

public class LocalizationTests
{
    static StringLocalizer<SharedResource> Localizer()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }), NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    static T WithCulture<T>(string culture, Func<T> f)
    {
        var (ui, c) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo(culture);
            return f();
        }
        finally
        {
            CultureInfo.CurrentUICulture = ui;
            CultureInfo.CurrentCulture = c;
        }
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-CN")] // browsers send zh-CN; it falls back to zh-Hans
    public void Chinese_StringsLoadFromSatelliteAssembly(string culture)
    {
        var l = Localizer();
        Assert.Equal("过", WithCulture(culture, () => l["Pass"].Value));
        Assert.Equal("剩 12 张", WithCulture(culture, () => l["{0} left", 12].Value));
    }

    [Theory]
    [InlineData("zh-Hant")]
    [InlineData("zh-HK")] // Hong Kong and Taiwan browsers fall back to zh-Hant
    [InlineData("zh-TW")]
    public void TraditionalChinese_StringsLoad(string culture)
    {
        var l = Localizer();
        Assert.Equal("過", WithCulture(culture, () => l["Pass"].Value));
        Assert.Equal("{0} 出銃".Replace("{0}", "Ann"), WithCulture(culture, () => l["On {0}'s discard", "Ann"].Value));
        Assert.Equal(Script.Traditional, WithCulture(culture, () => Languages.Current));
    }

    [Fact]
    public void English_ReturnsTheKey()
    {
        var l = Localizer();
        Assert.Equal("Pass", WithCulture("en", () => l["Pass"].Value));
    }

    [Fact]
    public void GameText_TileAndBotNames()
    {
        var t = new GameText(Localizer());
        Assert.Equal("三条", WithCulture("zh-Hans", () => t.TileName(Tiles.Parse("3s")[0])));
        Assert.Equal("红中", WithCulture("zh-Hans", () => t.TileName(Tiles.Red)));
        Assert.Equal("电脑·林", WithCulture("zh-Hans", () => t.BotName(0)));
        Assert.Equal("3 Bamboo", WithCulture("en", () => t.TileName(Tiles.Parse("3s")[0])));
        Assert.Equal("Bot Lin", WithCulture("en", () => t.BotName(0)));
        Assert.Equal("三索", WithCulture("zh-Hant", () => t.TileName(Tiles.Parse("3s")[0])));
        Assert.Equal("紅中", WithCulture("zh-Hant", () => t.TileName(Tiles.Red)));
        Assert.Equal("電腦·陳", WithCulture("zh-Hant", () => t.BotName(1)));
    }

    [Fact]
    public void GameText_LogLines()
    {
        var t = new GameText(Localizer());
        var entry = new MjLogEntry(MjLogKind.Pung, 1, 0, Tiles.Red);
        string Name(int s) => s == 1 ? "Ann" : "Bo";
        Assert.Equal("Ann 碰 红中", WithCulture("zh-Hans", () => t.LogLine(entry, Name)));
        Assert.Equal("Ann pungs Red Dragon", WithCulture("en", () => t.LogLine(entry, Name)));
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public void EveryKeyInTheCode_HasATranslation(string culture)
    {
        var project = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "GameWeb");
        var resx = XDocument.Load(Path.Combine(project, "Resources", $"SharedResource.{culture}.resx"));
        var translated = resx.Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();

        var pattern = new Regex(@"\b[Ll]\[""((?:[^""\\]|\\.)*)""");
        var files = Directory.GetFiles(Path.Combine(project, "Components"), "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(project, "Localization"), "*.cs"));
        var missing = files
            .SelectMany(f => pattern.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .Where(k => !translated.Contains(k))
            .ToList();
        Assert.True(missing.Count == 0, $"Missing {culture} translations (add to translations.{culture}.py and run build_resx.py): " + string.Join(" | ", missing));
    }

    static string ThisFile([CallerFilePath] string path = "") => path;
}
