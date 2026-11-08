using GameWeb.Games.Xiangqi;

namespace GameWeb.Tests;

public class XiangqiClockAndDrawTests
{
    static XqMove M(string s) => XqMove.Parse(s);

    /// <summary>A started host-vs-guest room (host plays Red) on a test clock.</summary>
    static (XiangqiRoom Room, Func<int, DateTime> Advance) TwoPlayers(XqRoomSettings settings)
    {
        var room = new XiangqiRoom("XQC" + Random.Shared.Next(100, 999), settings);
        var now = DateTime.UtcNow;
        room.Clock = () => now;
        room.Connect("host", "Host");
        room.Connect("guest", "Guest");
        Assert.True(room.Start("host"));
        room.TickNow();
        // Move time forward a second at a time, ticking as the real timer would.
        DateTime Advance(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                now += TimeSpan.FromSeconds(1);
                room.TickNow();
            }
            return now;
        }
        return (room, Advance);
    }

    [Fact]
    public void GameClock_RunsForTheSideToMove_AndAddsTheIncrement()
    {
        var (room, advance) = TwoPlayers(new XqRoomSettings { ClockMinutes = 1, IncrementSeconds = 5 });
        using var _ = room;
        advance(10);
        Assert.True(room.Move("host", M("h2e2")));
        var view = room.GetView("guest");
        Assert.Equal(55, view.RedClock, 1);     // 60 - 10 + 5
        Assert.Equal(60, view.BlackClock, 1);
        advance(3);
        Assert.Equal(57, room.GetView("guest").BlackClock, 1);
        Assert.Null(room.GetView("guest").SecondsLeft);   // no per-move timer alongside a clock
    }

    [Fact]
    public void GameClock_RunningOut_Loses()
    {
        var (room, advance) = TwoPlayers(new XqRoomSettings { ClockMinutes = 1, IncrementSeconds = 0 });
        using var _ = room;
        advance(59);
        Assert.False(room.GetView("host").IsOver);
        advance(2);
        var view = room.GetView("host");
        Assert.Equal(XqResult.BlackWins, view.Result);
        Assert.Equal(XqEndReason.Timeout, view.Reason);
    }

    [Fact]
    public void DrawOffer_CanBeDeclined_ThenAccepted()
    {
        var (room, _) = TwoPlayers(new XqRoomSettings { MoveSeconds = 0 });
        using var r = room;
        Assert.True(room.GetView("host").CanOfferDraw);
        Assert.True(room.OfferDraw("host"));
        Assert.Equal(XqSide.Red, room.GetView("guest").DrawOfferBy);
        Assert.False(room.GetView("host").CanOfferDraw);           // one offer at a time
        Assert.False(room.AnswerDraw("host", accept: true));      // can't accept your own offer

        Assert.True(room.AnswerDraw("guest", accept: false));
        Assert.True(room.GetView("host").MyDrawDeclined);
        Assert.False(room.GetView("host").CanOfferDraw);           // must wait before offering again
        Assert.False(room.GetView("host").IsOver);

        Assert.True(room.OfferDraw("guest"));
        Assert.True(room.AnswerDraw("host", accept: true));
        var view = room.GetView("host");
        Assert.Equal(XqResult.Draw, view.Result);
        Assert.Equal(XqEndReason.Agreement, view.Reason);
    }

    [Fact]
    public void Moving_DeclinesADrawOffer()
    {
        var (room, _) = TwoPlayers(new XqRoomSettings { MoveSeconds = 0 });
        using var r = room;
        room.Move("host", M("h2e2"));
        room.OfferDraw("host");
        Assert.True(room.Move("guest", M("h9g7")));
        Assert.Null(room.GetView("host").DrawOfferBy);
        Assert.True(room.GetView("host").MyDrawDeclined);
        room.Move("host", M("h0g2"));
        Assert.False(room.GetView("host").MyDrawDeclined);       // the notice goes once you move on
    }

    [Fact]
    public async Task Computer_DeclinesADrawInAnEvenOpening()
    {
        using var room = new XiangqiRoom("XQD01", new XqRoomSettings { QuickPlay = true, MoveSeconds = 0, BotLevel = XqLevel.Medium });
        room.BotDrawReplyDelay = TimeSpan.Zero;
        room.Connect("host", "Host");
        Assert.True(room.OfferDraw("host"));
        for (int i = 0; i < 100 && room.GetView("host").DrawOfferBy is not null; i++) await Task.Delay(50);
        var view = room.GetView("host");
        Assert.Null(view.DrawOfferBy);
        Assert.True(view.MyDrawDeclined);
        Assert.False(view.IsOver);
    }

    [Theory]
    [InlineData(-400, 10, true)]     // clearly losing
    [InlineData(0, 10, false)]       // level, early on
    [InlineData(20, 80, true)]       // level, long game
    [InlineData(300, 80, false)]     // winning
    public void Computer_DrawPolicy(int score, int plies, bool accepts) =>
        Assert.Equal(accepts, XqBot.AcceptsDraw(score, plies));
}
