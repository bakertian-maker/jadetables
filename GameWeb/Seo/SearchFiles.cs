using System.Text;
using System.Xml.Linq;
using GameWeb.Localization;

namespace GameWeb.Seo;

/// <summary>
/// robots.txt and sitemap.xml for search engines. Only the public pages are listed; game rooms are
/// private (anyone with the link can join) and are kept out of search results.
/// </summary>
public static class SearchFiles
{
    /// <summary>Public pages, in the order search engines should see them.</summary>
    public static readonly string[] Pages = ["/", "/mahjong", "/mahjong/rules", "/xiangqi", "/xiangqi/rules", "/privacy"];

    /// <summary>Paths crawlers should skip: private rooms, the language switch and Blazor's connection.</summary>
    public static readonly string[] Blocked = ["/mahjong/room/", "/xiangqi/room/", "/culture/", "/_blazor", "/Error"];

    static readonly XNamespace Sm = "http://www.sitemaps.org/schemas/sitemap/0.9";
    static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    public static string RobotsTxt(string baseUrl)
    {
        var sb = new StringBuilder();
        sb.Append("User-agent: *\n");
        sb.Append("Allow: /\n");
        foreach (var path in Blocked) sb.Append("Disallow: ").Append(path).Append('\n');
        sb.Append('\n').Append("Sitemap: ").Append(baseUrl).Append("/sitemap.xml\n");
        return sb.ToString();
    }

    /// <summary>
    /// One entry per page, with the English, Simplified and Traditional Chinese versions linked as
    /// alternates (the ?culture= query picks the language) and the plain URL as the default.
    /// </summary>
    public static string SitemapXml(string baseUrl)
    {
        var urls = Pages.Select(page =>
        {
            string plain = baseUrl + page;
            var url = new XElement(Sm + "url", new XElement(Sm + "loc", plain));
            foreach (var culture in Languages.Supported)
                url.Add(Alternate(culture, $"{plain}?culture={culture}"));
            url.Add(Alternate("x-default", plain));
            return url;
        });
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(Sm + "urlset", new XAttribute(XNamespace.Xmlns + "xhtml", Xhtml), urls));
        using var writer = new Utf8StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    static XElement Alternate(string hreflang, string href) =>
        new(Xhtml + "link", new XAttribute("rel", "alternate"), new XAttribute("hreflang", hreflang), new XAttribute("href", href));

    /// <summary>The site's address: Site:BaseUrl from settings, or the address of this request.</summary>
    public static string BaseUrl(IConfiguration config, HttpRequest request) =>
        (config["Site:BaseUrl"] is { Length: > 0 } configured ? configured : $"{request.Scheme}://{request.Host}").TrimEnd('/');

    public static void MapSearchFiles(this WebApplication app)
    {
        app.MapGet("/robots.txt", (IConfiguration config, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Text(RobotsTxt(BaseUrl(config, http.Request)), "text/plain; charset=utf-8");
        });
        app.MapGet("/sitemap.xml", (IConfiguration config, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Text(SitemapXml(BaseUrl(config, http.Request)), "application/xml; charset=utf-8");
        });
    }

    sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
