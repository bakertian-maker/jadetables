using GameWeb.Localization;
using Microsoft.AspNetCore.Http;

namespace GameWeb.Tests;

public class LanguagePathsTests
{
    [Theory]
    [InlineData("/zh-cn/xiangqi", "/zh-cn", "/xiangqi", "zh-Hans")]
    [InlineData("/zh-hk/mahjong/room/ABCDE", "/zh-hk", "/mahjong/room/ABCDE", "zh-Hant")]
    [InlineData("/ZH-HK/xiangqi", "/zh-hk", "/xiangqi", "zh-Hant")]
    [InlineData("/zh-cn/", "/zh-cn", "/", "zh-Hans")]
    public void Prefix_IsMovedOutOfThePath(string path, string prefix, string rest, string culture)
    {
        Assert.True(LanguagePaths.TrySplit(path, out var p, out var r, out var c));
        Assert.Equal(prefix, p.Value!.ToLowerInvariant());
        Assert.Equal(rest, r.Value);
        Assert.Equal(culture, c);
    }

    [Theory]
    [InlineData("/xiangqi")]
    [InlineData("/")]
    [InlineData("/zh-cnx/xiangqi")]     // whole segments only
    [InlineData("/zh-tw/xiangqi")]
    public void OtherPaths_AreEnglish(string path) =>
        Assert.False(LanguagePaths.TrySplit(path, out _, out _, out _));

    [Fact]
    public void Prefixes_ForEachLanguage()
    {
        Assert.Equal("", LanguagePaths.PrefixFor("en"));
        Assert.Equal("/zh-cn", LanguagePaths.PrefixFor("zh-Hans"));
        Assert.Equal("/zh-hk", LanguagePaths.PrefixFor("zh-Hant"));
    }

    [Theory]
    [InlineData("zh-CN,zh;q=0.9,en;q=0.8", "zh-Hans")]
    [InlineData("zh-TW,zh;q=0.9", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("en-GB,en;q=0.9,zh-CN;q=0.5", "en")]
    [InlineData("fr-FR,fr;q=0.9", "en")]
    [InlineData("", "en")]
    public void FirstVisit_LanguageFromTheBrowser(string header, string expected) =>
        Assert.Equal(expected, LanguagePaths.FromAcceptLanguage(header));

    const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";
    const string IPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

    [Theory]
    [InlineData(null, "zh-CN,zh;q=0.9", Chrome, "zh-Hans")]       // browser language, on any page
    [InlineData(null, "zh-TW,zh;q=0.9", IPhone, "zh-Hant")]
    [InlineData(null, "en-US,en;q=0.9", Chrome, "en")]
    [InlineData("en", "zh-CN", Chrome, "en")]                      // a choice made on the site wins
    [InlineData("zh-Hant", "en-US", Chrome, "zh-Hant")]
    public void Visitors_GetTheirLanguage(string? saved, string accept, string ua, string expected) =>
        Assert.Equal(expected, LanguagePaths.PreferredLanguage(saved, accept, ua));

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
    [InlineData("WhatsApp/2.23.20.0")]
    [InlineData("TelegramBot (like TwitterBot)")]
    [InlineData("Mozilla/5.0 (compatible; Baiduspider/2.0; +http://www.baidu.com/search/spider.html)")]
    [InlineData("")]
    public void SearchEnginesAndPreviews_StayOnTheAddressTheyAskedFor(string ua)
    {
        Assert.True(LanguagePaths.IsBot(ua));
        Assert.Equal("en", LanguagePaths.PreferredLanguage(null, "zh-CN,zh;q=0.9", ua));
    }

    [Theory]
    [InlineData(Chrome)]
    [InlineData(IPhone)]
    [InlineData("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Mobile Safari/537.36 MicroMessenger/8.0")]
    public void RealBrowsers_AreNotBots(string ua) => Assert.False(LanguagePaths.IsBot(ua));
}
