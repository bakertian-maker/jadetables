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
            Assert.NotNull(service.TryCreateMahjong(new MjRoomSettings()));
        Assert.Null(service.TryCreateMahjong(new MjRoomSettings()));
    }

    [Fact]
    public void RoomService_FindsRoomsWhereThePlayerHasASeat()
    {
        using var service = new RoomService(NullLogger<RoomService>.Instance);
        var mine = service.TryCreateMahjong(new MjRoomSettings())!;
        var other = service.TryCreateMahjong(new MjRoomSettings())!;
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
