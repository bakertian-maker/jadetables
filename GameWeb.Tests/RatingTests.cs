using GameWeb.Data;
using GameWeb.Games.Xiangqi;
using GameWeb.Localization;

namespace GameWeb.Tests;

public class RatingTests
{
    [Fact]
    public void EvenGame_WinGainsWhatALossLoses()
    {
        int win = Ratings.Update(1200, 1200, 1, gamesPlayed: 30);
        int loss = Ratings.Update(1200, 1200, 0, gamesPlayed: 30);
        Assert.Equal(1212, win);
        Assert.Equal(1188, loss);
        Assert.Equal(1200, Ratings.Update(1200, 1200, 0.5, gamesPlayed: 30));
    }

    [Fact]
    public void NewPlayers_MoveFaster()
    {
        Assert.Equal(1220, Ratings.Update(1200, 1200, 1, gamesPlayed: 0));
        Assert.Equal(1212, Ratings.Update(1200, 1200, 1, gamesPlayed: 20));
    }

    [Fact]
    public void BeatingAWeakerOpponent_GainsLittle()
    {
        int gain = Ratings.Update(1600, Ratings.BotRating(XqLevel.Beginner), 1, gamesPlayed: 30) - 1600;
        Assert.InRange(gain, 0, 2);
        int drop = 1200 - Ratings.Update(1200, Ratings.BotRating(XqLevel.Difficult), 0, gamesPlayed: 30);
        Assert.InRange(drop, 1, 3);   // losing to a much stronger opponent costs little
    }

    [Fact]
    public void ComputerLevels_AreOrdered() =>
        Assert.True(Ratings.BotRating(XqLevel.Beginner) < Ratings.BotRating(XqLevel.Medium)
            && Ratings.BotRating(XqLevel.Medium) < Ratings.BotRating(XqLevel.Difficult));

    [Theory]
    [InlineData("/zh-cn/xiangqi", "zh-Hant", "/zh-hk/xiangqi")]
    [InlineData("/xiangqi?x=1", "zh-Hans", "/zh-cn/xiangqi?x=1")]
    [InlineData("/zh-hk/account", "en", "/account")]
    [InlineData("/zh-cn", "en", "/")]
    public void SameAddress_InAnotherLanguage(string path, string culture, string expected) =>
        Assert.Equal(expected, LanguagePaths.InLanguage(path, culture));
}
