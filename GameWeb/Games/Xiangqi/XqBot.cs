using System.Diagnostics;

namespace GameWeb.Games.Xiangqi;

/// <summary>Computer strength. A stronger engine level (Pikafish) is planned to come after Difficult.</summary>
public enum XqLevel { Beginner, Medium, Difficult }

/// <summary>
/// Computer player: iterative-deepening alpha-beta (negamax) with a capture search and
/// material plus position-table evaluation. Works on its own copy of the position, so it
/// can run off the room lock.
///
/// Beginner looks 1 move ahead and often settles for a merely decent move.
/// Medium looks 3 moves ahead with a little variety.
/// Difficult deepens until its time is up (usually 6 to 8 moves ahead), helped by a
/// transposition table, killer moves and check extensions.
/// </summary>
public static class XqBot
{
    const int Mate = 100_000;
    const int Infinity = 1_000_000;
    const int MaxQuiescePly = 8;
    const int MaxPly = 64;

    /// <summary>Rough piece values for move ordering (captures of valuable pieces first); not the evaluation.</summary>
    static readonly int[] PieceValue = [0, 0, 200, 200, 400, 900, 450, 100];

    sealed record LevelSettings(int MaxDepth, TimeSpan Budget, int Margin, double LooseChance, int LooseMargin, bool Enhanced);

    static LevelSettings For(XqLevel level) => level switch
    {
        XqLevel.Beginner => new(1, TimeSpan.FromMilliseconds(200), Margin: 120, LooseChance: 0.25, LooseMargin: 350, Enhanced: false),
        XqLevel.Medium => new(3, TimeSpan.FromMilliseconds(900), Margin: 15, LooseChance: 0, LooseMargin: 0, Enhanced: false),
        _ => new(MaxPly / 8, TimeSpan.FromMilliseconds(2000), Margin: 0, LooseChance: 0, LooseMargin: 0, Enhanced: true),
    };

    public static XqMove ChooseMove(XqPosition position, XqLevel level, Random? rng = null, TimeSpan? budget = null,
        XqGameContext? context = null) =>
        Analyse(position, level, rng, budget, context: context).Move;

    /// <summary>
    /// What the engine needs to know about the game so far: earlier positions (repeating one counts as a draw
    /// in the search) and moves to avoid because they lose by a repetition rule. Take it under the game's
    /// lock; it is a copy.
    /// </summary>
    public sealed record XqGameContext(IReadOnlyCollection<ulong> Seen, IReadOnlySet<XqMove> Avoid)
    {
        public static XqGameContext Of(XqGame game) =>
            new(game.RepetitionHistory().ToHashSet(), game.MovesThatLoseByRepetition());
    }

    /// <summary>A move and its score, in points for the side to move (a soldier is 100; near ±100000 means a forced mate).</summary>
    public readonly record struct Analysis(XqMove Move, int Score);

    /// <summary>
    /// Chooses a move like <see cref="ChooseMove"/> and also returns its score.
    /// <paramref name="variety"/> &gt; 0 makes any level pick at random among moves within that many points of
    /// the best, and among at most <paramref name="maxChoices"/> of them (self-play uses this so games don't all
    /// follow the same line); 0 keeps the level's own style.
    /// </summary>
    public static Analysis Analyse(XqPosition position, XqLevel level, Random? rng = null, TimeSpan? budget = null,
        int variety = 0, int maxChoices = int.MaxValue, XqEval? eval = null, XqGameContext? context = null)
    {
        rng ??= Random.Shared;
        var settings = For(level);
        bool fullRootWindow = !settings.Enhanced || variety > 0;
        var search = new Search(position.Clone(), budget ?? settings.Budget, settings.Enhanced, fullRootWindow, eval ?? XqEval.Default)
        {
            Seen = context?.Seen.ToHashSet() ?? [],
            Avoid = context?.Avoid,
        };
        var scores = search.Run(settings.MaxDepth);
        if (scores.Count == 0) throw new InvalidOperationException("No legal moves");

        int best = scores.Max(s => s.Score);
        if (!fullRootWindow)
        {
            // Difficult: other moves' scores are only bounds, so take the first move that reached the best score.
            return new Analysis(scores.First(s => s.Score == best).Move, best);
        }
        // Never throw away a forced win or walk into a forced loss on purpose.
        bool decided = Math.Abs(best) > Mate / 2;
        int margin = decided ? 0 : variety > 0 ? variety : settings.Margin;
        if (!decided && variety == 0 && settings.LooseChance > 0 && rng.NextDouble() < settings.LooseChance) margin = settings.LooseMargin;
        var choices = scores.Where(s => s.Score >= best - margin && s.Score > -Mate / 2)
            .OrderByDescending(s => s.Score)
            .Take(variety > 0 ? Math.Max(1, maxChoices) : int.MaxValue)
            .ToList();
        if (choices.Count == 0) choices = scores.Where(s => s.Score == best).ToList();
        var pick = choices[rng.Next(choices.Count)];
        return new Analysis(pick.Move, pick.Score);
    }

    /// <summary>Static evaluation from the side to move's point of view, with the evaluation in use.</summary>
    public static int Evaluate(XqPosition pos) => XqEval.Default.Evaluate(pos);

    /// <summary>
    /// True if the position is quiet: the side to move isn't in check and the capture search can't
    /// improve on the static evaluation. Evaluation tuning only learns from quiet positions.
    /// </summary>
    public static bool IsQuiet(XqPosition pos, XqEval eval)
    {
        if (pos.InCheck(pos.SideToMove)) return false;
        var search = new Search(pos.Clone(), TimeSpan.FromSeconds(10), enhanced: false, fullRootWindow: true, eval);
        return search.QuiescenceScore() == eval.Evaluate(pos);
    }

    readonly record struct RootScore(XqMove Move, int Score);

    enum Bound : byte { None, Exact, Lower, Upper }

    struct TtEntry
    {
        public ulong Key;
        public int Score;
        public XqMove Best;
        public short Depth;
        public Bound Bound;
    }

    sealed class Search(XqPosition pos, TimeSpan budget, bool enhanced, bool fullRootWindow, XqEval eval)
    {
        const int TableSize = 1 << 16;

        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly TtEntry[]? _table = enhanced ? new TtEntry[TableSize] : null;
        readonly XqMove[,] _killers = new XqMove[MaxPly + 1, 2];
        readonly bool[,] _hasKiller = new bool[MaxPly + 1, 2];
        readonly ulong[] _path = new ulong[MaxPly + 2];
        ulong _hash;
        long _nodes;
        bool _stopped;

        /// <summary>Positions from the game before the root: reaching one again scores as a draw.</summary>
        public HashSet<ulong> Seen { get; init; } = [];
        /// <summary>Root moves not to play unless nothing else is legal.</summary>
        public IReadOnlySet<XqMove>? Avoid { get; init; }

        /// <summary>True if the current position (at ply &gt;= 1) repeats one from the game or the search path.</summary>
        bool IsRepetition(int ply)
        {
            _path[ply] = _hash;
            if (Seen.Contains(_hash)) return true;
            // The hash includes the side to move, so only every second ply can match.
            for (int k = ply - 2; k >= 0; k -= 2)
                if (_path[k] == _hash) return true;
            return false;
        }

        /// <summary>The capture-search score of the position (side to move's point of view).</summary>
        public int QuiescenceScore() => Quiesce(-Infinity, Infinity, 0);

        public List<RootScore> Run(int maxDepth)
        {
            var root = pos.LegalMoves();
            var completed = new List<RootScore>();
            if (root.Count == 0) return completed;
            if (Avoid is { Count: > 0 } && root.Any(m => !Avoid.Contains(m))) root.RemoveAll(Avoid.Contains);
            if (root.Count == 1) return [new RootScore(root[0], 0)];
            _hash = XqZobrist.Hash(pos);
            _path[0] = _hash;
            Order(root, default, false, 0);
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                var scores = new List<RootScore>(root.Count);
                int alpha = -Infinity;
                foreach (var m in root)
                {
                    var undo = MakeMove(m);
                    // A full window gives every root move a true score to choose from (Beginner, Medium,
                    // and self-play variety); Difficult on its own only needs the best move.
                    int score = fullRootWindow
                        ? -Negamax(depth - 1, -Infinity, Infinity, 1)
                        : -Negamax(depth - 1, -Infinity, -alpha, 1);
                    UnmakeMove(m, undo);
                    if (_stopped) break;
                    scores.Add(new RootScore(m, score));
                    alpha = Math.Max(alpha, score);
                }
                if (_stopped) break;
                completed = scores;
                root = scores.OrderByDescending(s => s.Score).Select(s => s.Move).ToList();
                if (Math.Abs(alpha) > Mate / 2) break; // a forced result is found; deeper search adds nothing
            }
            return completed;
        }

        XqUndo MakeMove(XqMove m)
        {
            _hash = XqZobrist.After(_hash, pos, m);
            return pos.Make(m);
        }

        void UnmakeMove(XqMove m, XqUndo undo)
        {
            pos.Unmake(m, undo);
            _hash = XqZobrist.After(_hash, pos, m); // XOR is its own inverse
        }

        bool TimeUp()
        {
            if ((++_nodes & 1023) == 0 && _clock.Elapsed > budget) _stopped = true;
            return _stopped;
        }

        int Negamax(int depth, int alpha, int beta, int ply)
        {
            if (TimeUp()) return 0;
            if (IsRepetition(ply)) return 0;
            var mover = pos.SideToMove;
            bool inCheck = enhanced && pos.InCheck(mover);
            if (inCheck && ply < MaxPly / 2) depth++; // look further when in check
            if (depth <= 0 || ply >= MaxPly) return Quiesce(alpha, beta, 0);

            int alphaStart = alpha;
            XqMove ttMove = default;
            bool hasTtMove = false;
            if (_table is not null)
            {
                ref var e = ref _table[_hash & (TableSize - 1)];
                if (e.Key == _hash && e.Bound != Bound.None)
                {
                    ttMove = e.Best;
                    hasTtMove = true;
                    if (e.Depth >= depth)
                    {
                        if (e.Bound == Bound.Exact) return e.Score;
                        if (e.Bound == Bound.Lower && e.Score >= beta) return e.Score;
                        if (e.Bound == Bound.Upper && e.Score <= alpha) return e.Score;
                    }
                }
            }

            var moves = new List<XqMove>(48);
            pos.GeneratePseudoLegal(moves);
            Order(moves, ttMove, hasTtMove, ply);
            int legal = 0, bestScore = -Infinity;
            XqMove bestMove = default;
            foreach (var m in moves)
            {
                bool quiet = pos[m.To] == 0;
                var undo = MakeMove(m);
                if (pos.InCheck(mover)) { UnmakeMove(m, undo); continue; }
                legal++;
                int score = -Negamax(depth - 1, -beta, -alpha, ply + 1);
                UnmakeMove(m, undo);
                if (_stopped) return 0;
                if (score > bestScore) { bestScore = score; bestMove = m; }
                if (score > alpha) alpha = score;
                if (alpha >= beta)
                {
                    if (enhanced && quiet) AddKiller(ply, m);
                    break;
                }
            }
            // No legal move loses in Xiangqi (checkmate or stalemate); prefer the slowest loss.
            if (legal == 0) return -Mate + ply;

            if (_table is not null && Math.Abs(bestScore) < Mate / 2)
            {
                ref var e = ref _table[_hash & (TableSize - 1)];
                if (e.Bound == Bound.None || e.Key != _hash || depth >= e.Depth)
                {
                    e.Key = _hash;
                    e.Score = bestScore;
                    e.Best = bestMove;
                    e.Depth = (short)depth;
                    e.Bound = bestScore >= beta ? Bound.Lower : bestScore <= alphaStart ? Bound.Upper : Bound.Exact;
                }
            }
            return bestScore;
        }

        int Quiesce(int alpha, int beta, int qply)
        {
            if (TimeUp()) return 0;
            int stand = eval.Evaluate(pos);
            if (stand >= beta || qply >= MaxQuiescePly) return stand;
            if (stand > alpha) alpha = stand;

            var moves = new List<XqMove>(16);
            pos.GeneratePseudoLegal(moves, capturesOnly: true);
            Order(moves, default, false, -1);
            var mover = pos.SideToMove;
            foreach (var m in moves)
            {
                var undo = MakeMove(m);
                if (pos.InCheck(mover)) { UnmakeMove(m, undo); continue; }
                int score = -Quiesce(-beta, -alpha, qply + 1);
                UnmakeMove(m, undo);
                if (_stopped) return 0;
                if (score >= beta) return score;
                if (score > alpha) alpha = score;
            }
            return alpha;
        }

        void AddKiller(int ply, XqMove m)
        {
            if (_hasKiller[ply, 0] && _killers[ply, 0] == m) return;
            _killers[ply, 1] = _killers[ply, 0];
            _hasKiller[ply, 1] = _hasKiller[ply, 0];
            _killers[ply, 0] = m;
            _hasKiller[ply, 0] = true;
        }

        /// <summary>
        /// Best move from the table first, then captures (most valuable victim, least valuable attacker),
        /// then killer moves, then the rest.
        /// </summary>
        void Order(List<XqMove> moves, XqMove ttMove, bool hasTtMove, int ply)
        {
            int Priority(XqMove m)
            {
                if (hasTtMove && m == ttMove) return 1_000_000;
                int victim = pos[m.To];
                if (victim != 0)
                {
                    int victimType = Pieces.Type(victim);
                    int victimValue = victimType == Pieces.General ? 10_000 : PieceValue[victimType];
                    return 100_000 + victimValue * 10 - PieceValue[Pieces.Type(pos[m.From])] / 10;
                }
                if (ply >= 0)
                {
                    if (_hasKiller[ply, 0] && _killers[ply, 0] == m) return 50_000;
                    if (_hasKiller[ply, 1] && _killers[ply, 1] == m) return 40_000;
                }
                return 0;
            }
            moves.Sort((a, b) => Priority(b).CompareTo(Priority(a)));
        }
    }
}
