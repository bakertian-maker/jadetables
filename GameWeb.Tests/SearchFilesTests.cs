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
    public void Robots_BlocksRoomsInEveryLanguage()
    {
        var robots = SearchFiles.RobotsTxt(Base);
        Assert.Contains("Disallow: /zh-cn/xiangqi/room/", robots);
        Assert.Contains("Disallow: /zh-hk/mahjong/room/", robots);
    }

    [Fact]
    public void Sitemap_ListsEveryPageInEachLanguage_WithItsAlternates()
    {
        var doc = XDocument.Parse(SearchFiles.SitemapXml(Base));
        XNamespace sm = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var urls = doc.Root!.Elements(sm + "url").ToList();
        var locs = urls.Select(u => u.Element(sm + "loc")!.Value).ToList();

        Assert.Equal(SearchFiles.Pages.Length * 3, urls.Count);
        Assert.Contains("https://www.jadetables.com/", locs);
        Assert.Contains("https://www.jadetables.com/zh-cn/", locs);
        Assert.Contains("https://www.jadetables.com/zh-hk/xiangqi/rules", locs);
        Assert.Contains("https://www.jadetables.com/zh-cn/mahjong", locs);
        foreach (var url in urls)
        {
            var links = url.Elements(xhtml + "link").ToDictionary(l => (string)l.Attribute("hreflang")!, l => (string)l.Attribute("href")!);
            Assert.Equal(["en", "zh-Hans", "zh-Hant", "x-default"], links.Keys);
            Assert.Contains(url.Element(sm + "loc")!.Value, links.Values);   // each entry lists itself
        }
        Assert.DoesNotContain("/room/", doc.ToString());
        Assert.DoesNotContain("culture=", doc.ToString());
    }
}
