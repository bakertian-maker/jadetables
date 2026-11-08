namespace GameWeb.Games.Xiangqi;

public enum SelfPlayEnding { Rules, Resignation, MoveLimitWin, MoveLimitDraw }

public sealed record SelfPlayGame(XqGame Game, XqResult Result, SelfPlayEnding Ending)
{
    public IReadOnlyList<XqMove> Moves => Game.History.Select(h => h.Move).ToList();
}

/// <summary>
/// The computer playing itself to produce games for the opening book.
/// Each game starts from a mainstream opening (<see cref="SelfPlaySettings.Openings"/>), because the engine's
/// own first-move choices at short thinking times are often unusual. After that the opening is varied in
/// two stages: first among only the top few moves, then a little more, so games still branch.
/// Clearly decided games are stopped early (adjudicated), as is usual in engine testing; the rules
/// engine still ends games by checkmate, stalemate, the 60-move rule and so on.
/// </summary>
public static class XqSelfPlay
{
    /// <param name="redEval">Evaluation for Red (default: the one in use); a match gives each side its own.</param>
    public static SelfPlayGame Play(SelfPlaySettings settings, Random rng, XqEval? redEval = null, XqEval? blackEval = null)
    {
        var game = new XqGame();
        foreach (var written in settings.PickOpening(rng))
        {
            var move = XqNotationParser.Parse(game.Position, written, out var error)
                ?? throw new InvalidOperationException($"Opening move {written}: {error}");
            game.TryMove(move);
        }
        int redLeadPlies = 0, blackLeadPlies = 0;
        int lastRedScore = 0;

        while (!game.IsOver && game.History.Count < settings.MaxPlies)
        {
            var (variety, maxChoices) = settings.VarietyFor(game.History.Count);
            var eval = game.SideToMove == XqSide.Red ? redEval : blackEval;
            var analysis = XqBot.Analyse(game.Position, settings.Level, rng, settings.MoveBudget, variety, maxChoices, eval);
            // Score from Red's point of view, for adjudication.
            int redScore = game.SideToMove == XqSide.Red ? analysis.Score : -analysis.Score;
            lastRedScore = redScore;
            redLeadPlies = redScore >= settings.ResignScore ? redLeadPlies + 1 : 0;
            blackLeadPlies = redScore <= -settings.ResignScore ? blackLeadPlies + 1 : 0;

            if (!game.TryMove(analysis.Move)) throw new InvalidOperationException($"Engine chose an illegal move {analysis.Move}");

            if (redLeadPlies >= settings.ResignAfterPlies) return new(game, XqResult.RedWins, SelfPlayEnding.Resignation);
            if (blackLeadPlies >= settings.ResignAfterPlies) return new(game, XqResult.BlackWins, SelfPlayEnding.Resignation);
        }

        if (game.IsOver) return new(game, game.Result, SelfPlayEnding.Rules);
        if (lastRedScore >= settings.MoveLimitWinScore) return new(game, XqResult.RedWins, SelfPlayEnding.MoveLimitWin);
        if (lastRedScore <= -settings.MoveLimitWinScore) return new(game, XqResult.BlackWins, SelfPlayEnding.MoveLimitWin);
        return new(game, XqResult.Draw, SelfPlayEnding.MoveLimitDraw);
    }
}

public sealed class SelfPlaySettings
{
    public XqLevel Level { get; init; } = XqLevel.Difficult;
    /// <summary>Thinking time per move. Shorter than live play so many games can be played.</summary>
    public TimeSpan MoveBudget { get; init; } = TimeSpan.FromMilliseconds(300);
    /// <summary>
    /// Common first-move pairs with rough popularity weights. Each game starts with one of them.
    /// Written as plain opening knowledge; an empty list lets the engine choose from the first move.
    /// </summary>
    public IReadOnlyList<(string Moves, int Weight)> Openings { get; init; } =
    [
        ("炮二平五 马8进7", 3),   // 中炮对屏风马 / 进马
        ("炮二平五 炮8平5", 2),   // 顺炮
        ("炮二平五 炮2平5", 1),   // 列炮
        ("炮二平五 马2进3", 1),   // 中炮对左马
        ("兵七进一 卒7进1", 2),   // 仙人指路对兵
        ("兵七进一 炮2平3", 1),   // 仙人指路对卒底炮
        ("相三进五 卒7进1", 1),   // 飞相局
        ("相三进五 马8进7", 1),
        ("马二进三 卒7进1", 1),   // 起马局
        ("马二进三 马8进7", 1),
    ];

    /// <summary>The written moves of a randomly chosen opening, weighted by popularity.</summary>
    public IReadOnlyList<string> PickOpening(Random rng)
    {
        if (Openings.Count == 0) return [];
        int pick = rng.Next(Openings.Sum(o => o.Weight));
        foreach (var (moves, weight) in Openings)
        {
            if (pick < weight) return moves.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            pick -= weight;
        }
        return [];
    }

    /// <summary>Stage 1: the first half-moves after the opening pick among the top few moves only.</summary>
    public int StartPlies { get; init; } = 4;
    /// <summary>Stage 1: how far below the best score (points; a soldier is 100) a move may be...</summary>
    public int StartVariety { get; init; } = 10;
    /// <summary>...and how many of the top moves are allowed.</summary>
    public int StartChoices { get; init; } = 3;
    /// <summary>Stage 2: up to this half-move, a little more variety so games branch within an opening.</summary>
    public int VarietyPlies { get; init; } = 10;
    public int Variety { get; init; } = 25;
    public int VarietyChoices { get; init; } = 4;

    /// <summary>(variety in points, maximum choices) for the move at this half-move number; (0, 1) = best move.</summary>
    public (int Variety, int MaxChoices) VarietyFor(int ply) =>
        ply < StartPlies ? (StartVariety, StartChoices)
        : ply < VarietyPlies ? (Variety, VarietyChoices)
        : (0, 1);

    /// <summary>A side this far ahead (a chariot is 900)...</summary>
    public int ResignScore { get; init; } = 700;
    /// <summary>...for this many half-moves in a row wins: the other side "resigns".</summary>
    public int ResignAfterPlies { get; init; } = 6;
    /// <summary>Games stop here; the side ahead by <see cref="MoveLimitWinScore"/> wins, otherwise a draw.</summary>
    public int MaxPlies { get; init; } = 200;
    public int MoveLimitWinScore { get; init; } = 300;
}
