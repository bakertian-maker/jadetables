using GameWeb.Games.Xiangqi;

namespace GameWeb.Games.Archive;

public enum GameType : byte { Mahjong = 1, Xiangqi = 2 }

/// <param name="Side">Xiangqi: 0 Red, 1 Black. Mahjong: null.</param>
/// <param name="PlayerId">The browser's player id; the store keeps only a hash of it.</param>
public sealed record FinishedPlayer(int Seat, byte? Side, string? PlayerId, bool IsBot, byte? BotLevel, int? Score);

/// <param name="BookMoves">Xiangqi opening moves to add to the book: position hash and move (from * 90 + to).</param>
public sealed record FinishedGame(
    GameType Type, string RoomCode, DateTime StartedAt, DateTime EndedAt,
    byte Result, byte Reason, int PlyCount, string? Moves, string? Details,
    IReadOnlyList<FinishedPlayer> Players,
    IReadOnlyList<(ulong Hash, int Move)> BookMoves);

/// <summary>Stores finished games. <see cref="Save"/> only queues the game, so it never slows a room down.</summary>
public interface IGameArchive
{
    void Save(FinishedGame game);
}

public readonly record struct BookMove(XqMove Move, int RedWins, int BlackWins, int Draws)
{
    public int Games => RedWins + BlackWins + Draws;

    /// <summary>Share of points (win 1, draw 0.5) for the side making this move.</summary>
    public double ScoreFor(XqSide side) =>
        Games == 0 ? 0 : ((side == XqSide.Red ? RedWins : BlackWins) + Draws * 0.5) / Games;
}

/// <summary>Moves played from a position in earlier games, and how those games ended.</summary>
public interface IOpeningBook
{
    Task<IReadOnlyList<BookMove>> LookupAsync(ulong positionHash, CancellationToken cancellationToken);
}

/// <summary>Used when no database is configured (and in tests): saves nothing, knows no openings.</summary>
public sealed class NullGameStore : IGameArchive, IOpeningBook
{
    public static readonly NullGameStore Instance = new();

    public void Save(FinishedGame game) { }

    public Task<IReadOnlyList<BookMove>> LookupAsync(ulong positionHash, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BookMove>>([]);
}
