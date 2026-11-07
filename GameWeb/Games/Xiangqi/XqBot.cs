using System.Diagnostics;

namespace GameWeb.Games.Xiangqi;

public enum XqLevel { Easy, Medium }

/// <summary>
/// Computer player: iterative-deepening alpha-beta (negamax) with a capture search,
/// capture-first move ordering, and material plus position-table evaluation.
/// Easy searches 2 plies and picks among near-best moves; Medium searches 4 plies.
/// Works on its own copy of the position, so it can run off the room lock.
/// </summary>
public static class XqBot
{
    const int Mate = 100_000;
    const int Infinity = 1_000_000;
    const int MaxQuiescePly = 8;

    static readonly int[] PieceValue = [0, 0, 200, 200, 400, 900, 450, 100];

    public static XqMove ChooseMove(XqPosition position, XqLevel level, Random? rng = null, TimeSpan? budget = null)
    {
        rng ??= Random.Shared;
        var search = new Search(position.Clone(), budget ?? (level == XqLevel.Easy ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMilliseconds(1500)));
        int maxDepth = level == XqLevel.Easy ? 2 : 4;
        var scores = search.Run(maxDepth);
        if (scores.Count == 0) throw new InvalidOperationException("No legal moves");

        int best = scores.Max(s => s.Score);
        if (level == XqLevel.Medium)
        {
            var top = scores.Where(s => s.Score == best).ToList();
            return top[rng.Next(top.Count)].Move;
        }
        // Easy: any move within a small margin of the best, so it plays sensibly but varies and misses things.
        var good = scores.Where(s => s.Score >= best - 60 && s.Score > -Mate / 2).ToList();
        if (good.Count == 0) good = scores.Where(s => s.Score == best).ToList();
        return good[rng.Next(good.Count)].Move;
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

    sealed class Search(XqPosition pos, TimeSpan budget)
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        long _nodes;
        bool _stopped;

        public List<RootScore> Run(int maxDepth)
        {
            var root = pos.LegalMoves();
            var completed = new List<RootScore>();
            if (root.Count == 0) return completed;
            if (root.Count == 1) return [new RootScore(root[0], 0)];
            Order(root);
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                var scores = new List<RootScore>(root.Count);
                foreach (var m in root)
                {
                    var undo = pos.Make(m);
                    int score = -Negamax(depth - 1, -Infinity, Infinity, 1);
                    pos.Unmake(m, undo);
                    if (_stopped) break;
                    scores.Add(new RootScore(m, score));
                }
                if (_stopped) break;
                completed = scores;
                // Search the best moves first on the next iteration.
                root = scores.OrderByDescending(s => s.Score).Select(s => s.Move).ToList();
            }
            return completed;
        }

        bool TimeUp()
        {
            if ((++_nodes & 1023) == 0 && _clock.Elapsed > budget) _stopped = true;
            return _stopped;
        }

        int Negamax(int depth, int alpha, int beta, int ply)
        {
            if (TimeUp()) return 0;
            if (depth <= 0) return Quiesce(alpha, beta, 0);

            var moves = new List<XqMove>(48);
            pos.GeneratePseudoLegal(moves);
            Order(moves);
            var mover = pos.SideToMove;
            int legal = 0;
            foreach (var m in moves)
            {
                var undo = pos.Make(m);
                if (pos.InCheck(mover)) { pos.Unmake(m, undo); continue; }
                legal++;
                int score = -Negamax(depth - 1, -beta, -alpha, ply + 1);
                pos.Unmake(m, undo);
                if (_stopped) return 0;
                if (score >= beta) return score;
                if (score > alpha) alpha = score;
            }
            // No legal move loses in Xiangqi (checkmate or stalemate); prefer the slowest loss.
            return legal == 0 ? -Mate + ply : alpha;
        }

        int Quiesce(int alpha, int beta, int qply)
        {
            if (TimeUp()) return 0;
            int stand = Evaluate(pos);
            if (stand >= beta || qply >= MaxQuiescePly) return stand;
            if (stand > alpha) alpha = stand;

            var moves = new List<XqMove>(16);
            pos.GeneratePseudoLegal(moves, capturesOnly: true);
            Order(moves);
            var mover = pos.SideToMove;
            foreach (var m in moves)
            {
                var undo = pos.Make(m);
                if (pos.InCheck(mover)) { pos.Unmake(m, undo); continue; }
                int score = -Quiesce(-beta, -alpha, qply + 1);
                pos.Unmake(m, undo);
                if (_stopped) return 0;
                if (score >= beta) return score;
                if (score > alpha) alpha = score;
            }
            return alpha;
        }

        /// <summary>Captures first, most valuable victim by least valuable attacker; generals as victims first of all.</summary>
        void Order(List<XqMove> moves)
        {
            int Key(XqMove m)
            {
                int victim = pos[m.To];
                if (victim == 0) return 0;
                int victimType = Pieces.Type(victim);
                int victimValue = victimType == Pieces.General ? 10_000 : PieceValue[victimType];
                return victimValue * 10 - PieceValue[Pieces.Type(pos[m.From])] / 10 + 1;
            }
            moves.Sort((a, b) => Key(b).CompareTo(Key(a)));
        }
    }
}
