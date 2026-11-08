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
}
