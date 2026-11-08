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

    static readonly int[] PieceValue = [0, 0, 200, 200, 400, 900, 450, 100];

    sealed record LevelSettings(int MaxDepth, TimeSpan Budget, int Margin, double LooseChance, int LooseMargin, bool Enhanced);

    static LevelSettings For(XqLevel level) => level switch
    {
        XqLevel.Beginner => new(1, TimeSpan.FromMilliseconds(200), Margin: 120, LooseChance: 0.25, LooseMargin: 350, Enhanced: false),
        XqLevel.Medium => new(3, TimeSpan.FromMilliseconds(900), Margin: 15, LooseChance: 0, LooseMargin: 0, Enhanced: false),
        _ => new(MaxPly / 8, TimeSpan.FromMilliseconds(2000), Margin: 0, LooseChance: 0, LooseMargin: 0, Enhanced: true),
    };

    public static XqMove ChooseMove(XqPosition position, XqLevel level, Random? rng = null, TimeSpan? budget = null)
    {
        rng ??= Random.Shared;
        var settings = For(level);
        var search = new Search(position.Clone(), budget ?? settings.Budget, settings.Enhanced);
        var scores = search.Run(settings.MaxDepth);
        if (scores.Count == 0) throw new InvalidOperationException("No legal moves");

        int best = scores.Max(s => s.Score);
        // Difficult: other moves' scores are only bounds, so take the first move that reached the best score.
        if (settings.Enhanced) return scores.First(s => s.Score == best).Move;
        // Never throw away a forced win or walk into a forced loss on purpose.
        bool decided = Math.Abs(best) > Mate / 2;
        int margin = decided ? 0 : settings.Margin;
        if (!decided && settings.LooseChance > 0 && rng.NextDouble() < settings.LooseChance) margin = settings.LooseMargin;
        var choices = scores.Where(s => s.Score >= best - margin && s.Score > -Mate / 2).ToList();
        if (choices.Count == 0) choices = scores.Where(s => s.Score == best).ToList();
        return choices[rng.Next(choices.Count)].Move;
    }

    /// <summary>Static evaluation from the side to move's point of view.</summary>
    public static int Evaluate(XqPosition pos)
    {
        int score = 0;
        for (int sq = 0; sq < 90; sq++)
        {
            int piece = pos[sq];
            if (piece == 0) continue;
            int v = PieceScore(piece, sq);
            score += Pieces.SideOf(piece) == XqSide.Red ? v : -v;
        }
        return pos.SideToMove == XqSide.Red ? score : -score;
    }

    static int PieceScore(int piece, int sq)
    {
        var side = Pieces.SideOf(piece);
        int file = Sq.File(sq);
        int rank = side == XqSide.Red ? Sq.Rank(sq) : 9 - Sq.Rank(sq); // ranks from the owner's side
        int centre = Math.Abs(file - 4);
        int type = Pieces.Type(piece);
        int v = PieceValue[type];
        switch (type)
        {
            case Pieces.Soldier:
                if (rank >= 5) v += 90 + (rank == 9 ? -40 : (rank - 5) * 12) + (centre <= 1 ? 20 : 0);
                else if (rank == 4) v += 10;
                break;
            case Pieces.Horse:
                v += -centre * 6 + (rank is >= 2 and <= 7 ? 15 : 0) + (rank >= 5 ? 10 : 0);
                break;
            case Pieces.Chariot:
                v += (rank >= 3 ? 10 : 0) + (centre is 1 ? 6 : 0);
                break;
            case Pieces.Cannon:
                v += (file == 4 ? 15 : 0) + (rank == 2 ? 5 : 0);
                break;
            case Pieces.General:
                v += file == 4 ? 0 : -10;
                break;
        }
        return v;
    }

    readonly record struct RootScore(XqMove Move, int Score);

    // ---- Zobrist hashing for the transposition table ----

    static readonly ulong[,] PieceKeys = BuildKeys();
    static readonly ulong BlackToMoveKey = 0x9E3779B97F4A7C15UL;

    static ulong[,] BuildKeys()
    {
        var rng = new Random(20261108); // fixed seed: hashes are only compared within one search
        var keys = new ulong[15, 90];
        for (int p = 0; p < 15; p++)
            for (int sq = 0; sq < 90; sq++)
                keys[p, sq] = (ulong)rng.NextInt64() ^ ((ulong)rng.NextInt64() << 1);
        return keys;
    }

    static ulong Key(int piece, int sq) => PieceKeys[piece + 7, sq];

    static ulong Hash(XqPosition pos)
    {
        ulong h = pos.SideToMove == XqSide.Black ? BlackToMoveKey : 0;
        for (int sq = 0; sq < 90; sq++)
            if (pos[sq] != 0) h ^= Key(pos[sq], sq);
        return h;
    }

    enum Bound : byte { None, Exact, Lower, Upper }

    struct TtEntry
    {
        public ulong Key;
        public int Score;
        public XqMove Best;
        public short Depth;
        public Bound Bound;
    }

    sealed class Search(XqPosition pos, TimeSpan budget, bool enhanced)
    {
        const int TableSize = 1 << 16;

        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly TtEntry[]? _table = enhanced ? new TtEntry[TableSize] : null;
        readonly XqMove[,] _killers = new XqMove[MaxPly + 1, 2];
        readonly bool[,] _hasKiller = new bool[MaxPly + 1, 2];
        ulong _hash;
        long _nodes;
        bool _stopped;

        public List<RootScore> Run(int maxDepth)
        {
            var root = pos.LegalMoves();
            var completed = new List<RootScore>();
            if (root.Count == 0) return completed;
            if (root.Count == 1) return [new RootScore(root[0], 0)];
            _hash = Hash(pos);
            Order(root, default, false, 0);
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                var scores = new List<RootScore>(root.Count);
                int alpha = -Infinity;
                foreach (var m in root)
                {
                    var undo = MakeMove(m);
                    // Beginner and Medium give every root move a full window, so each has a true score
                    // to choose from; Difficult only needs the best move and searches the rest against it.
                    int score = enhanced
                        ? -Negamax(depth - 1, -Infinity, -alpha, 1)
                        : -Negamax(depth - 1, -Infinity, Infinity, 1);
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
            int piece = pos[m.From], captured = pos[m.To];
            _hash ^= Key(piece, m.From) ^ Key(piece, m.To) ^ BlackToMoveKey;
            if (captured != 0) _hash ^= Key(captured, m.To);
            return pos.Make(m);
        }

        void UnmakeMove(XqMove m, XqUndo undo)
        {
            pos.Unmake(m, undo);
            int piece = pos[m.From];
            _hash ^= Key(piece, m.From) ^ Key(piece, m.To) ^ BlackToMoveKey;
            if (undo.Captured != 0) _hash ^= Key(undo.Captured, m.To);
        }

        bool TimeUp()
        {
            if ((++_nodes & 1023) == 0 && _clock.Elapsed > budget) _stopped = true;
            return _stopped;
        }

        int Negamax(int depth, int alpha, int beta, int ply)
        {
            if (TimeUp()) return 0;
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
            int stand = Evaluate(pos);
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
