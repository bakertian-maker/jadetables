using System.Globalization;

namespace GameWeb.Localization;

public enum Script { Latin, Simplified, Traditional }

public static class Languages
{
    public const string English = "en";
    public const string SimplifiedChinese = "zh-Hans";
    public const string TraditionalChinese = "zh-Hant";

    public static readonly string[] Supported = [English, SimplifiedChinese, TraditionalChinese];

    /// <summary>Short labels for the header switch, in each language's own script.</summary>
    public static readonly (string Culture, string Short, string Name)[] Menu =
    [
        (English, "EN", "English"),
        (SimplifiedChinese, "简", "简体中文"),
        (TraditionalChinese, "繁", "繁體中文"),
    ];

    /// <summary>The script of the current UI language (zh-TW and zh-HK resolve to Traditional).</summary>
    public static Script Current
    {
        get
        {
            for (var c = CultureInfo.CurrentUICulture; c != CultureInfo.InvariantCulture; c = c.Parent)
            {
                if (c.Name == TraditionalChinese) return Script.Traditional;
                if (c.Name == SimplifiedChinese) return Script.Simplified;
            }
            return Script.Latin;
        }
    }

    public static bool IsChinese => Current != Script.Latin;
    public static bool IsTraditional => Current == Script.Traditional;

    public static string CurrentCulture => Current switch
    {
        Script.Traditional => TraditionalChinese,
        Script.Simplified => SimplifiedChinese,
        _ => English,
    };

    public static string HtmlLang => CurrentCulture;

    /// <summary>
    /// True for a path on this site ("/mahjong?x=1"), false for anything a browser could treat as
    /// another site ("//evil.com", "/\evil.com") or that contains control characters.
    /// </summary>
    public static bool IsSafeLocalPath(string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && !url.StartsWith("//")
        && !url.Any(c => c == '\\' || char.IsControl(c));
}
