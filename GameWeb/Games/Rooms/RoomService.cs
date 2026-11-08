using System.Collections.Concurrent;
using System.Security.Cryptography;
using GameWeb.Games.Archive;

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
    readonly IGameArchive _archive;
    readonly IOpeningBook _book;

    public RoomService(ILogger<RoomService> logger, IGameArchive? archive = null, IOpeningBook? book = null)
    {
        _logger = logger;
        _archive = archive ?? NullGameStore.Instance;
        _book = book ?? NullGameStore.Instance;
        _cleanup = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public int Count => _rooms.Count;

    /// <summary>
    /// Creates a room with a fresh code, or returns null when the server already has
    /// <see cref="MaxRooms"/> open. <paramref name="create"/> builds the room for a given code.
    /// </summary>
    public T? TryCreate<T>(Func<string, T> create) where T : Room
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
            var room = create(code);
            room.Archive = _archive;
            room.Book = _book;
            if (_rooms.TryAdd(code, room))
            {
                _logger.LogInformation("Created {Game} room {Code}", room.GameKey, code);
                return room;
            }
            room.Dispose();
        }
    }

    public Room? Get(string code) => _rooms.TryGetValue(code.Trim(), out var r) ? r : null;

    /// <summary>Unfinished rooms where this player holds a seat (optionally of one game), most recent first.</summary>
    public IReadOnlyList<Room> RoomsWithSeatFor(string playerId, string? gameKey = null) =>
        string.IsNullOrEmpty(playerId)
            ? []
            : _rooms.Values.Where(r => r.HasSeat(playerId) && !r.IsFinished && (gameKey is null || r.GameKey == gameKey))
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
