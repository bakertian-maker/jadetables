using GameWeb.Games.Archive;

namespace GameWeb.Games.Xiangqi;

/// <summary>
/// Opening book rules: which moves of a finished game go into the book, and how the computer
/// picks a book move. Pure logic; the database lives behind <see cref="IOpeningBook"/>.
/// </summary>
public static class XqBook
{
    /// <summary>Only the first 12 half-moves (6 moves each side) go into the book.</summary>
    public const int MaxPlies = 12;
    /// <summary>A book move needs at least this many games behind it.</summary>
    public const int MinGames = 3;
    /// <summary>Shorter games (early resignations, abandoned games) are left out.</summary>
    public const int MinGamePlies = 10;

    /// <summary>
    /// The (position hash, move) pairs of a finished game's opening, keeping only moves by sides that
    /// <paramref name="trusted"/> accepts (people and the Difficult computer, not weaker bots).
    /// </summary>
    public static List<(ulong Hash, int Move)> Entries(XqGame game, Func<XqSide, bool> trusted)
    {
        if (!game.IsOver || game.History.Count < MinGamePlies || game.Reason == XqEndReason.Timeout) return [];
        return OpeningEntries(game.History.Select(h => h.Move), trusted);
    }

    /// <summary>(position hash, move) for the first <see cref="MaxPlies"/> moves of a game from the start position.</summary>
    public static List<(ulong Hash, int Move)> OpeningEntries(IEnumerable<XqMove> moves, Func<XqSide, bool>? trusted = null)
    {
        var result = new List<(ulong, int)>();
        var pos = XqPosition.Start();
        ulong hash = XqZobrist.Hash(pos);
        foreach (var m in moves.Take(MaxPlies))
        {
            if (trusted is null || trusted(pos.SideToMove)) result.Add((hash, m.From * 90 + m.To));
            hash = XqZobrist.After(hash, pos, m);
            pos.Make(m);
        }
        return result;
    }

    /// <summary>
    /// A book move for this position, or null to search instead. Only legal moves with enough games
    /// and a good score are considered; more popular, better-scoring moves are picked more often.
    /// Difficult only plays the best-scoring moves; Medium is a little more adventurous.
    /// </summary>
    public static XqMove? Pick(XqPosition pos, IReadOnlyList<BookMove> entries, XqLevel level, Random rng)
    {
        if (level == XqLevel.Beginner || entries.Count == 0) return null;
        var side = pos.SideToMove;
        var legal = pos.LegalMoves();
        var candidates = entries
            .Where(e => e.Games >= MinGames && legal.Contains(e.Move))
            .Select(e => (e.Move, Score: e.ScoreFor(side), e.Games))
            .Where(c => c.Score >= (level == XqLevel.Difficult ? 0.5 : 0.4))
            .ToList();
        if (candidates.Count == 0) return null;

        if (level == XqLevel.Difficult)
        {
            double best = candidates.Max(c => c.Score);
            candidates = candidates.Where(c => c.Score >= best - 0.1).ToList();
        }
        double total = candidates.Sum(c => c.Games * c.Score * c.Score);
        double pick = rng.NextDouble() * total;
        foreach (var c in candidates)
        {
            pick -= c.Games * c.Score * c.Score;
            if (pick <= 0) return c.Move;
        }
        return candidates[^1].Move;
    }

    public static XqMove DecodeMove(int encoded) => new(encoded / 90, encoded % 90);
}
