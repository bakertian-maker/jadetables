using GameWeb.Accounts;
using GameWeb.Data;
using GameWeb.Games.Archive;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameWeb.Tests;

public class AccountTests
{
    static readonly byte[] Mine = SqlGameStore.PlayerKey("my-browser");
    static readonly HashSet<string> MyKeys = [Convert.ToHexString(Mine)];

    static GameRecord Xiangqi(byte result, byte mySide, byte reason = 1) => new()
    {
        Id = 1, GameType = GameType.Xiangqi, Result = result, Reason = reason, PlyCount = 41, EndedAt = DateTime.UtcNow,
        Players =
        [
            new() { Seat = 0, Side = mySide, PlayerKey = Mine },
            new() { Seat = 1, Side = (byte)(1 - mySide), IsBot = true, BotLevel = (byte)Games.Xiangqi.XqLevel.Difficult },
        ],
    };

    [Theory]
    [InlineData(1, 0, GameOutcome.Win)]    // Red wins, I'm Red
    [InlineData(1, 1, GameOutcome.Loss)]   // Red wins, I'm Black
    [InlineData(2, 1, GameOutcome.Win)]
    [InlineData(3, 0, GameOutcome.Draw)]
    public void Xiangqi_ResultIsSeenFromMySide(byte result, byte side, GameOutcome expected)
    {
        var entry = AccountStore.Describe(Xiangqi(result, side), MyKeys)!;
        Assert.Equal(expected, entry.Outcome);
        Assert.Equal(side, entry.Side);
        var opponent = Assert.Single(entry.Opponents);
        Assert.True(opponent.IsBot);
        Assert.Equal((byte)Games.Xiangqi.XqLevel.Difficult, opponent.BotLevel);
    }

    [Fact]
    public void Mahjong_PlaceComesFromTheScores()
    {
        var g = new GameRecord
        {
            GameType = GameType.Mahjong, Result = 1, PlyCount = 8,
            Players =
            [
                new() { Seat = 0, PlayerKey = Mine, Score = 40 },
                new() { Seat = 1, IsBot = true, Score = 120 },
                new() { Seat = 2, IsBot = true, Score = -60 },
                new() { Seat = 3, PlayerKey = SqlGameStore.PlayerKey("friend"), Score = -100 },
            ],
        };
        var entry = AccountStore.Describe(g, MyKeys)!;
        Assert.Equal(2, entry.Place);
        Assert.Equal(40, entry.Score);
        Assert.Equal(GameOutcome.Played, entry.Outcome);
        Assert.Equal(3, entry.Opponents.Count);
        Assert.Equal(2, entry.Opponents.Count(o => o.IsBot));
    }

    [Fact]
    public void GamesWithoutMe_AreSkipped()
    {
        var g = Xiangqi(1, 0);
        g.Players[0].PlayerKey = SqlGameStore.PlayerKey("someone-else");
        Assert.Null(AccountStore.Describe(g, MyKeys));
    }

    [Theory]
    [InlineData("  Ann  ", "ann@example.com", "Ann")]
    [InlineData("", "dong.tian@example.com", "dong.tian")]
    [InlineData(null, null, "Player")]
    [InlineData("A name that is far too long for the site", "x@y.z", "A name that is far t")]
    public void DisplayName_IsCleaned(string? name, string? email, string expected) =>
        Assert.Equal(expected, AccountSetup.CleanDisplayName(name, email));

    [Fact]
    public void Emails_AreSpacedOut_PerAccount()
    {
        var emails = new AccountEmails(Options.Create(new EmailOptions()), new PassThrough(), NullLogger<AccountEmails>.Instance);
        Assert.False(emails.CanSend);
        Assert.True(emails.TryReserve("user-1"));
        Assert.False(emails.TryReserve("user-1"));   // again within two minutes
        Assert.True(emails.TryReserve("user-2"));
    }

    [Fact]
    public async Task WithoutSmtp_EmailsAreLoggedNotSent()
    {
        var emails = new AccountEmails(Options.Create(new EmailOptions()), new PassThrough(), NullLogger<AccountEmails>.Instance);
        var user = new ApplicationUser { Email = "ann@example.com", DisplayName = "Ann", Culture = "zh-Hant" };
        await emails.SendConfirmationAsync(user, "https://www.jadetables.com/zh-hk/account/confirm-email?x=1");   // must not throw
    }

    sealed class PassThrough : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
