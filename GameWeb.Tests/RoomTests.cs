using GameWeb.Games.Xiangqi;
using GameWeb.Games.Mahjong;
using GameWeb.Games.Rooms;
using GameWeb.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameWeb.Tests;

public class RoomTests
{
    /// <summary>A room with no game, exposing seat maintenance at a chosen time.</summary>
    sealed class TestRoom() : Room("TEST1", 4)
    {
        public override string GameKey => "test";
        protected override bool Tick(DateTime now) => false;
        public override bool Start(string playerId) => false;
        public bool RunMaintenance(DateTime now) => MaintainSeats(now);
    }

    [Fact]
    public void WaitingRoom_FreesSeatOfPlayerWhoLeft_WhileHostStays()
    {
        using var room = new TestRoom();
        room.Connect("host", "Host");
        room.Connect("friend", "Friend");
        room.Disconnect("friend");

        Assert.False(room.RunMaintenance(DateTime.UtcNow)); // still within the grace period
        Assert.False(room.Seats[1].IsEmpty);

        Assert.True(room.RunMaintenance(DateTime.UtcNow + Room.ReconnectGrace + TimeSpan.FromSeconds(1)));
        Assert.True(room.Seats[1].IsEmpty);
        Assert.Equal("host", room.HostId);
    }

    [Fact]
    public void HostRole_MovesToAConnectedPlayer_WhenHostIsGone()
    {
        using var room = new TestRoom();
        room.Connect("host", "Host");
        room.Connect("friend", "Friend");
        room.Disconnect("host");

        room.RunMaintenance(DateTime.UtcNow + Room.ReconnectGrace + TimeSpan.FromSeconds(1));
        Assert.Equal("friend", room.HostId);
    }

    [Fact]
    public void RoomService_RefusesRoomsBeyondTheCap()
    {
        using var service = new RoomService(NullLogger<RoomService>.Instance);
        for (int i = 0; i < RoomService.MaxRooms; i++)
            Assert.NotNull(service.TryCreate(c => new MahjongRoom(c, new MjRoomSettings())));
        Assert.Null(service.TryCreate(c => new MahjongRoom(c, new MjRoomSettings())));
    }

    [Fact]
    public void RoomService_FindsRoomsWhereThePlayerHasASeat()
    {
        using var service = new RoomService(NullLogger<RoomService>.Instance);
        var mine = service.TryCreate(c => new MahjongRoom(c, new MjRoomSettings()))!;
        var other = service.TryCreate(c => new MahjongRoom(c, new MjRoomSettings()))!;
        mine.Connect("me", "Me");
        other.Connect("someone-else", "Other");

        Assert.Equal([mine.Code], service.RoomsWithSeatFor("me").Select(r => r.Code));
        Assert.Empty(service.RoomsWithSeatFor("stranger"));
        Assert.Empty(service.RoomsWithSeatFor(""));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/mahjong/room/ABCDE?x=1#faan", true)]
    [InlineData("//example.com", false)]
    [InlineData("/\\example.com", false)]
    [InlineData("https://example.com", false)]
    [InlineData("mahjong", false)]
    [InlineData("/a\nb", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeLocalPath(string? url, bool expected)
    {
        Assert.Equal(expected, Languages.IsSafeLocalPath(url));
    }
}

public class XiangqiRoomTests
{
    static XqMove M(string s) => XqMove.Parse(s);

    [Fact]
    public void HostPlaysRed_ByDefault_AndOnlyTheSideToMoveCanMove()
    {
        using var room = new XiangqiRoom("XQ001", new XqRoomSettings());
        room.Connect("host", "Host");
        room.Connect("guest", "Guest");
        Assert.True(room.Start("host"));

        var hostView = room.GetView("host");
        Assert.Equal(XqSide.Red, hostView.MySide);
        Assert.True(hostView.MyTurn);
        Assert.Contains(Sq.At(2, 7), hostView.MyMoves.Keys); // the cannon on h2 can move

        Assert.False(room.Move("guest", M("h9g7")));        // Black cannot move first
        Assert.True(room.Move("host", M("h2e2")));
        Assert.True(room.Move("guest", M("h9g7")));
        Assert.Equal(2, room.GetView("guest").History.Count);
    }

    [Fact]
    public void GuestCanPlayRed()
    {
        using var room = new XiangqiRoom("XQ002", new XqRoomSettings { Red = XqRedChoice.Guest });
        room.Connect("host", "Host");
        room.Connect("guest", "Guest");
        room.Start("host");
        Assert.Equal(XqSide.Red, room.GetView("guest").MySide);
        Assert.Equal(XqSide.Black, room.GetView("host").MySide);
    }

    [Fact]
    public void Resign_EndsTheGame_AndRematchSwapsSides()
    {
        using var room = new XiangqiRoom("XQ003", new XqRoomSettings());
        room.Connect("host", "Host");
        room.Connect("guest", "Guest");
        room.Start("host");
        Assert.True(room.Resign("guest"));
        var view = room.GetView("host");
        Assert.Equal(XqResult.RedWins, view.Result);
        Assert.Equal(XqEndReason.Resignation, view.Reason);
        Assert.True(room.IsFinished);

        Assert.True(room.Rematch("guest"));
        Assert.Equal(XqSide.Red, room.GetView("guest").MySide);
        Assert.False(room.IsFinished);
    }

    [Fact]
    public void Spectators_GetNoMoves()
    {
        using var room = new XiangqiRoom("XQ004", new XqRoomSettings());
        room.Connect("host", "Host");
        room.Connect("guest", "Guest");
        room.Start("host");
        Assert.Equal(JoinOutcome.Spectating, room.Connect("watcher", "Watcher"));
        var view = room.GetView("watcher");
        Assert.Null(view.MySide);
        Assert.Empty(view.MyMoves);
        Assert.False(room.Move("watcher", M("h2e2")));
    }

    [Fact]
    public async Task QuickPlay_ComputerAnswersTheFirstMove()
    {
        using var room = new XiangqiRoom("XQ005", new XqRoomSettings { QuickPlay = true, BotLevel = XqLevel.Beginner });
        room.Connect("me", "Me");
        Assert.True(room.Move("me", M("h2e2")));
        for (int i = 0; i < 50 && room.GetView("me").History.Count < 2; i++) await Task.Delay(100);
        Assert.Equal(2, room.GetView("me").History.Count);
        Assert.True(room.GetView("me").MyTurn);
    }
}
