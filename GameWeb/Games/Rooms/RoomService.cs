using System.Collections.Concurrent;
using System.Security.Cryptography;
using GameWeb.Games.Mahjong;

namespace GameWeb.Games.Rooms;

/// <summary>In-memory registry of open rooms. Rooms with nobody connected are closed after a while.</summary>
public sealed class RoomService : IDisposable
{
    const string CodeChars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    static readonly TimeSpan UnjoinedTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Most rooms open at once; each runs its own timer, so this bounds server load.</summary>
    public const int MaxRooms = 500;

    readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer _cleanup;
    readonly ILogger<RoomService> _logger;

    public RoomService(ILogger<RoomService> logger)
    {
        _logger = logger;
        _cleanup = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public int Count => _rooms.Count;

    /// <summary>Creates a room, or returns null when the server already has <see cref="MaxRooms"/> open.</summary>
    public MahjongRoom? TryCreateMahjong(MjRoomSettings settings)
    {
        if (_rooms.Count >= MaxRooms) Cleanup();
        if (_rooms.Count >= MaxRooms)
        {
            _logger.LogWarning("Room limit of {Max} reached", MaxRooms);
            return null;
        }
        while (true)
        {
            var code = RandomNumberGenerator.GetString(CodeChars, 5);
            var room = new MahjongRoom(code, settings);
            if (_rooms.TryAdd(code, room))
            {
                _logger.LogInformation("Created mahjong room {Code}", code);
                return room;
            }
            room.Dispose();
        }
    }

    public Room? Get(string code) => _rooms.TryGetValue(code.Trim(), out var r) ? r : null;

    /// <summary>Unfinished rooms where this player holds a seat, most recently active first.</summary>
    public IReadOnlyList<Room> RoomsWithSeatFor(string playerId) =>
        string.IsNullOrEmpty(playerId)
            ? []
            : _rooms.Values.Where(r => r.HasSeat(playerId) && !r.IsFinished)
                .OrderByDescending(r => r.LastActivity)
                .Take(3)
                .ToList();

    void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var (code, room) in _rooms)
        {
            if (room.HasConnectedHumans) continue;
            var timeout = room.EverJoined ? IdleTimeout : UnjoinedTimeout;
            if (now - room.LastActivity < timeout) continue;
            if (_rooms.TryRemove(code, out _))
            {
                room.Dispose();
                _logger.LogInformation("Closed idle room {Code}", code);
            }
        }
    }

    public void Dispose()
    {
        _cleanup.Dispose();
        foreach (var r in _rooms.Values) r.Dispose();
    }
}
