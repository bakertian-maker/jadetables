using System.Xml.Linq;
using GameWeb.Seo;

namespace GameWeb.Tests;

public class SearchFilesTests
{
    const string Base = "https://www.jadetables.com";

    [Fact]
    public void Robots_AllowsTheSite_BlocksRooms_AndPointsToTheSitemap()
    {
        var robots = SearchFiles.RobotsTxt(Base);
        Assert.Contains("User-agent: *", robots);
        Assert.Contains("Disallow: /xiangqi/room/", robots);
        Assert.Contains("Disallow: /mahjong/room/", robots);
        Assert.Contains("Sitemap: https://www.jadetables.com/sitemap.xml", robots);
    }

    [Fact]
    public void Sitemap_ListsEveryPublicPage_WithAllThreeLanguages()
    {
        var doc = XDocument.Parse(SearchFiles.SitemapXml(Base));
        XNamespace sm = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var urls = doc.Root!.Elements(sm + "url").ToList();

        Assert.Equal(SearchFiles.Pages.Select(p => Base + p), urls.Select(u => u.Element(sm + "loc")!.Value));
        foreach (var url in urls)
        {
            var langs = url.Elements(xhtml + "link").Select(l => (string)l.Attribute("hreflang")!).ToList();
            Assert.Equal(["en", "zh-Hans", "zh-Hant", "x-default"], langs);
        }
        Assert.DoesNotContain("/room/", doc.ToString());
        Assert.Contains("https://www.jadetables.com/xiangqi?culture=zh-Hant", doc.ToString());
    }
}
