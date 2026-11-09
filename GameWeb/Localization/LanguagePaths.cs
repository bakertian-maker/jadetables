using Microsoft.AspNetCore.Localization;
using Microsoft.Net.Http.Headers;

namespace GameWeb.Localization;

/// <summary>
/// The language is part of the address, so each language version has its own URL for search engines:
/// English at the root (/xiangqi), Simplified Chinese under /zh-cn (/zh-cn/xiangqi) and Traditional
/// Chinese, with Hong Kong mahjong terms, under /zh-hk (/zh-hk/xiangqi).
///
/// The prefix is moved from Request.Path to Request.PathBase, so the pages, static files and Blazor's
/// connection need no routes of their own: the page's &lt;base href&gt; is "/zh-cn/" and every link on the
/// site is relative to it, which keeps visitors in their language.
/// </summary>
public static class LanguagePaths
{
    public static readonly (string Prefix, string Culture)[] Prefixes =
    [
        ("/zh-cn", Languages.SimplifiedChinese),
        ("/zh-hk", Languages.TraditionalChinese),
    ];

    const string CultureItem = "LanguagePaths.Culture";

    /// <summary>The address prefix for a language: "" for English, "/zh-cn" or "/zh-hk".</summary>
    public static string PrefixFor(string culture) =>
        Prefixes.FirstOrDefault(p => p.Culture == culture).Prefix ?? "";

    /// <summary>Splits "/zh-cn/xiangqi" into ("/zh-cn", "/xiangqi", zh-Hans). Prefixes match whole segments only.</summary>
    public static bool TrySplit(PathString path, out PathString prefix, out PathString rest, out string culture)
    {
        foreach (var (p, c) in Prefixes)
        {
            if (path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase, out var remaining))
            {
                prefix = p;
                rest = remaining;
                culture = c;
                return true;
            }
        }
        prefix = rest = default;
        culture = Languages.English;
        return false;
    }

    /// <summary>Picks the language for a visitor with no saved choice, from the browser's Accept-Language.</summary>
    public static string FromAcceptLanguage(string? header)
    {
        if (string.IsNullOrEmpty(header) || !StringWithQualityHeaderValue.TryParseList(header.Split(','), out var list))
            return Languages.English;
        foreach (var lang in list.OrderByDescending(l => l.Quality ?? 1))
        {
            var tag = lang.Value.Value?.ToLowerInvariant() ?? "";
            if (tag.StartsWith("en")) return Languages.English;
            if (tag is "zh-hant" or "zh-tw" or "zh-hk" or "zh-mo" || tag.StartsWith("zh-hant")) return Languages.TraditionalChinese;
            if (tag.StartsWith("zh")) return Languages.SimplifiedChinese;
        }
        return Languages.English;
    }

    /// <summary>
    /// Moves a language prefix into PathBase, and redirects:
    /// <list type="bullet">
    /// <item>old ?culture= links to the matching address (permanently);</item>
    /// <item>"/zh-cn" to "/zh-cn/";</item>
    /// <item>a visitor whose saved choice (the language cookie) is Chinese, arriving at an English page, to
    /// the same page in their language; on the home page, a first-time visitor whose browser prefers
    /// Chinese too. Search engines send neither, so they always see each address in its own language.</item>
    /// </list>
    /// </summary>
    public static void UseLanguagePaths(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            var request = http.Request;
            bool page = HttpMethods.IsGet(request.Method) && IsPagePath(request.Path);

            if (TrySplit(request.Path, out var prefix, out var rest, out var culture))
            {
                if (page && !rest.HasValue)
                {
                    http.Response.Redirect(prefix + "/" + request.QueryString, permanent: true);
                    return;
                }
                if (page && request.Query["culture"] is { Count: > 0 } q)
                {
                    http.Response.Redirect(Target(q.ToString(), rest, request), permanent: true);
                    return;
                }
                request.PathBase = request.PathBase.Add(prefix);
                request.Path = rest;
                http.Items[CultureItem] = culture;
            }
            else if (page)
            {
                if (request.Query["culture"] is { Count: > 0 } q)
                {
                    http.Response.Redirect(Target(q.ToString(), request.Path, request), permanent: true);
                    return;
                }
                var saved = CookieRequestCultureProvider.ParseCookieValue(
                    request.Cookies[CookieRequestCultureProvider.DefaultCookieName] ?? "")?.UICultures.FirstOrDefault().Value;
                var wanted = saved is not null ? saved
                    : request.Path == "/" ? FromAcceptLanguage(request.Headers.AcceptLanguage)
                    : Languages.English;
                if (wanted != Languages.English && PrefixFor(wanted) is { Length: > 0 } p)
                {
                    http.Response.Redirect(p + request.Path + request.QueryString);
                    return;
                }
            }
            await next(http);
        });
    }

    /// <summary>The address of <paramref name="path"/> in a language, keeping the query apart from ?culture=.</summary>
    static string Target(string culture, PathString path, HttpRequest request)
    {
        if (!Languages.Supported.Contains(culture)) culture = Languages.English;
        var query = QueryString.Create(request.Query.Where(kv => kv.Key != "culture"));
        string p = path.HasValue ? path.Value! : "/";
        return PrefixFor(culture) + p + query;
    }

    /// <summary>Pages only: not static files (they have a dot), Blazor's connection, or the site's own endpoints.</summary>
    static bool IsPagePath(PathString path)
    {
        var p = path.Value ?? "/";
        if (p.StartsWith("/_") || p.StartsWith("/culture/", StringComparison.OrdinalIgnoreCase)) return false;
        int slash = p.LastIndexOf('/');
        return p.IndexOf('.', slash + 1) < 0;
    }

    /// <summary>Request localization reads the language from the address first.</summary>
    public sealed class PathCultureProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext http)
        {
            var culture = http.Items[CultureItem] as string ?? Languages.English;
            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(culture));
        }
    }

    /// <summary>Stores the visitor's language choice in the standard culture cookie, for a year.</summary>
    public static void RememberChoice(HttpResponse response, string culture) =>
        response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax, HttpOnly = true });

    /// <summary>The same local address in another language: "/zh-cn/xiangqi" → "/zh-hk/xiangqi".</summary>
    public static string InLanguage(string localPath, string culture)
    {
        PathString path = localPath.Split('?')[0];
        string query = localPath.Contains('?') ? localPath[localPath.IndexOf('?')..] : "";
        var rest = TrySplit(path, out _, out var r, out _) ? (r.HasValue ? r.Value! : "/") : (path.HasValue ? path.Value! : "/");
        return PrefixFor(culture) + rest + query;
    }

    /// <summary>The site-relative address of the current page with no language prefix ("/xiangqi").</summary>
    public static string PathWithoutLanguage(string baseRelativePath)
    {
        var path = "/" + baseRelativePath;
        int q = path.IndexOfAny(['?', '#']);
        return q < 0 ? path : path[..q];
    }

}
