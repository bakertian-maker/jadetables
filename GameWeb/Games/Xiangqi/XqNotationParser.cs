using System.Text.RegularExpressions;

namespace GameWeb.Games.Xiangqi;

/// <summary>
/// Reads moves written in Chinese notation ("炮二平五", "马8进7", "前车进一"), in Simplified or
/// Traditional characters, with Chinese numerals, ASCII or full-width digits. A written move is
/// matched against the position's legal moves, so only legal moves can come out.
/// </summary>
public static partial class XqNotationParser
{
    [GeneratedRegex(@"(?<t>[前中后後])?(?<p>[帅帥将將仕士相象马馬傌车車俥炮砲包兵卒])(?<f>[一二三四五六七八九1-9１-９])?(?<o>[进進退平])(?<d>[一二三四五六七八九1-9１-９])")]
    private static partial Regex MovePattern();

    /// <param name="Resolved">How many written moves fitted more than one legal move and were settled by looking ahead.</param>
    public sealed record ParsedGame(IReadOnlyList<string> Notations, IReadOnlyList<XqMove> Moves, string? Error, XqGame Game, int Resolved);

    /// <summary>All Chinese-notation moves in a text, in order (move numbers, headers and "*" prefixes are ignored).</summary>
    public static List<string> Tokens(string text) =>
        MovePattern().Matches(text).Select(m => m.Value).ToList();

    /// <summary>
    /// Replays a text game from the start position. Records sometimes leave out the front/rear marker
    /// (e.g. "炮五平六" with two cannons on file 5); such a move is settled by trying each reading and
    /// keeping the one with which the rest of the game is legal. On failure, Moves holds the longest
    /// legal prefix and Error names the first move that could not be played.
    /// </summary>
    public static ParsedGame ParseGame(string text)
    {
        var tokens = Tokens(text);
        var state = new SolveState();
        var moves = new List<XqMove>();
        bool ok = Solve(XqPosition.Start(), tokens, 0, moves, state);
        var played = ok ? moves : state.LongestPrefix;

        var game = new XqGame();
        string? error = ok ? null : $"Move {state.ErrorIndex + 1} ({tokens[state.ErrorIndex]}): {state.Error}";
        for (int i = 0; i < played.Count; i++)
        {
            if (game.IsOver)
            {
                error = $"Move {i + 1} ({tokens[i]}) comes after the game already ended";
                played = played.Take(i).ToList();
                break;
            }
            game.TryMove(played[i]);
        }
        return new ParsedGame(tokens, played, error, game, state.Resolved);
    }

    sealed class SolveState
    {
        public int ErrorIndex = -1;
        public string? Error;
        public List<XqMove> LongestPrefix = [];
        public int Resolved;
        public int Steps;
    }

    const int MaxSolveSteps = 100_000;

    static bool Solve(XqPosition pos, List<string> tokens, int i, List<XqMove> moves, SolveState state)
    {
        if (i == tokens.Count) return true;
        if (++state.Steps > MaxSolveSteps) return false;
        var candidates = Candidates(pos, tokens[i], out var error);
        if (candidates.Count == 0)
        {
            if (i > state.ErrorIndex)
            {
                state.ErrorIndex = i;
                state.Error = error;
                state.LongestPrefix = [.. moves];
            }
            return false;
        }
        if (candidates.Count > 1) state.Resolved++;
        foreach (var move in candidates)
        {
            var undo = pos.Make(move);
            moves.Add(move);
            if (Solve(pos, tokens, i + 1, moves, state)) return true;
            moves.RemoveAt(moves.Count - 1);
            pos.Unmake(move, undo);
        }
        if (candidates.Count > 1) state.Resolved--;
        return false;
    }

    /// <summary>The legal move that <paramref name="written"/> describes in this position, or null with a reason.</summary>
    public static XqMove? Parse(XqPosition pos, string written, out string? error)
    {
        var matches = Candidates(pos, written, out error);
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1) error = "more than one legal move matches";
        return null;
    }

    /// <summary>Every legal move that the written move could describe.</summary>
    public static List<XqMove> Candidates(XqPosition pos, string written, out string? error)
    {
        var matches = new List<XqMove>();
        var m = MovePattern().Match(written);
        if (!m.Success)
        {
            error = "not Chinese move notation";
            return matches;
        }
        int type = PieceType(m.Groups["p"].Value[0]);
        char op = m.Groups["o"].Value[0] switch { '进' or '進' => '+', '退' => '-', _ => '=' };
        int dest = Number(m.Groups["d"].Value[0]);
        int? fromFile = m.Groups["f"].Success ? Number(m.Groups["f"].Value[0]) : null;
        char? tandem = m.Groups["t"].Success ? m.Groups["t"].Value[0] : null;

        foreach (var move in pos.LegalMoves())
        {
            var n = XqNotation.Describe(pos, move);
            if (Pieces.Type(n.Piece) != type || n.Op != op || n.Dest != dest) continue;
            bool fits = tandem is char t
                ? n.Tandem > 0 && TandemMatches(t, n.Tandem, n.TandemCount)
                : n.FromFile == fromFile; // also accepts "车二进一" when two chariots share the file
            if (fits) matches.Add(move);
        }
        error = matches.Count == 0 ? "no legal move matches" : null;
        return matches;
    }

    static bool TandemMatches(char t, int index, int count) => t switch
    {
        '前' => index == 1,
        '中' => count == 3 && index == 2,
        _ => index == count, // 后 / 後: the rearmost
    };

    static int PieceType(char c) => c switch
    {
        '帅' or '帥' or '将' or '將' => Pieces.General,
        '仕' or '士' => Pieces.Advisor,
        '相' or '象' => Pieces.Elephant,
        '马' or '馬' or '傌' => Pieces.Horse,
        '车' or '車' or '俥' => Pieces.Chariot,
        '炮' or '砲' or '包' => Pieces.Cannon,
        _ => Pieces.Soldier,
    };

    static int Number(char c) => c switch
    {
        >= '1' and <= '9' => c - '0',
        >= '１' and <= '９' => c - '１' + 1,
        _ => "一二三四五六七八九".IndexOf(c) + 1,
    };
}
