namespace GameWeb.Games.Xiangqi;

public enum XqResult { Ongoing, RedWins, BlackWins, Draw }

// Stored in the database as numbers: add new reasons at the end only.
public enum XqEndReason
{
    None, Checkmate, Stalemate, Resignation, Timeout, NoCaptureLimit, NoAttackers, Agreement,
    /// <summary>Third repetition with neither side solely to blame: a draw.</summary>
    Repetition,
    /// <summary>Third repetition, one side checked with every move: that side loses.</summary>
    PerpetualCheck,
    /// <summary>Third repetition, one side chased with every move: that side loses.</summary>
    PerpetualChase,
}

/// <summary>
/// A move described the way players write it: piece, starting file, direction, destination.
/// Files are counted from each player's own right, 1..9. The UI formats this as
/// "炮二平五" (Chinese) or "C2.5" (WXF letters).
/// </summary>
/// <param name="Tandem">0 when the piece is alone on its file; otherwise 1 = front-most of <paramref name="TandemCount"/> same pieces on that file.</param>
/// <param name="Op">'+' forward, '-' backward, '=' sideways.</param>
/// <param name="Dest">Destination file for sideways or diagonal moves; ranks moved for straight forward/backward moves.</param>
public readonly record struct XqNotation(int Piece, int Tandem, int TandemCount, int FromFile, char Op, int Dest)
{
    public XqSide Side => Pieces.SideOf(Piece);

    public static int FileNumber(int file, XqSide side) => side == XqSide.Red ? 9 - file : file + 1;

    public static XqNotation Describe(XqPosition pos, XqMove m)
    {
        int piece = pos[m.From];
        var side = Pieces.SideOf(piece);
        int type = Pieces.Type(piece);
        int fr = Sq.Rank(m.From), ff = Sq.File(m.From), tr = Sq.Rank(m.To), tf = Sq.File(m.To);
        int forwardRanks = side == XqSide.Red ? tr - fr : fr - tr;

        char op = forwardRanks > 0 ? '+' : forwardRanks < 0 ? '-' : '=';
        bool diagonal = type is Pieces.Advisor or Pieces.Elephant or Pieces.Horse;
        int dest = diagonal || op == '=' ? FileNumber(tf, side) : Math.Abs(forwardRanks);

        // Same piece type on the same file: name them front / rear (or by order from the front).
        var sameFile = new List<int>();
        for (int r = 0; r <= 9; r++)
            if (pos[Sq.At(r, ff)] == piece) sameFile.Add(r);
        int tandem = 0;
        if (sameFile.Count > 1)
        {
            var fromFront = side == XqSide.Red ? sameFile.OrderByDescending(r => r) : sameFile.OrderBy(r => r);
            tandem = fromFront.ToList().IndexOf(fr) + 1;
        }
        return new XqNotation(piece, tandem, sameFile.Count, FileNumber(ff, side), op, dest);
    }
}

public sealed record XqMoveRecord(XqMove Move, int Piece, int Captured, XqNotation Notation, bool GaveCheck);

/// <summary>Xiangqi game state: position, history and the result. No timers or I/O.</summary>
public sealed class XqGame
{
    /// <summary>Draw after this many half-moves without a capture (60 moves each).</summary>
    public const int NoCaptureLimit = 120;

    readonly List<XqMoveRecord> _history = [];
    // For the repetition rules: the position after each half-move (index 0 = start), and whether each move
    // gave check or chased.
    readonly List<ulong> _hashes = [];
    readonly List<bool> _checks = [];
    readonly List<bool> _chases = [];
    readonly XqSide _firstMover;
    List<XqMove>? _legal;

    public XqGame(string? fen = null)
    {
        Position = fen is null ? XqPosition.Start() : XqPosition.FromFen(fen);
        _firstMover = Position.SideToMove;
        _hashes.Add(XqZobrist.Hash(Position));
        CheckForEnd();
    }

    public XqPosition Position { get; }
    public IReadOnlyList<XqMoveRecord> History => _history;
    public XqResult Result { get; private set; }
    public XqEndReason Reason { get; private set; }
    public bool IsOver => Result != XqResult.Ongoing;
    public XqSide SideToMove => Position.SideToMove;
    public bool InCheck => Position.InCheck(SideToMove);
    /// <summary>Changes on every move and when the game ends.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<XqMove> LegalMoves() => IsOver ? [] : _legal ??= Position.LegalMoves();

    public bool TryMove(XqMove m)
    {
        if (IsOver || !LegalMoves().Contains(m)) return false;
        var notation = XqNotation.Describe(Position, m);
        int piece = Position[m.From];
        var mover = Position.SideToMove;
        var undo = Position.Make(m);
        bool gaveCheck = Position.InCheck(Position.SideToMove);
        _history.Add(new XqMoveRecord(m, piece, undo.Captured, notation, gaveCheck));
        _hashes.Add(XqZobrist.Hash(Position));
        _checks.Add(gaveCheck);
        _chases.Add(XqRepetition.IsChasing(Position, mover));
        _legal = null;
        Version++;
        CheckForEnd();
        return true;
    }

    public void Resign(XqSide side) => End(Win(Pieces.Other(side)), XqEndReason.Resignation);

    public void LoseOnTime(XqSide side) => End(Win(Pieces.Other(side)), XqEndReason.Timeout);

    public void AgreeDraw() => End(XqResult.Draw, XqEndReason.Agreement);

    static XqResult Win(XqSide side) => side == XqSide.Red ? XqResult.RedWins : XqResult.BlackWins;

    void End(XqResult result, XqEndReason reason)
    {
        if (IsOver) return;
        Result = result;
        Reason = reason;
        _legal = null;
        Version++;
    }

    void CheckForEnd()
    {
        // In Xiangqi a side with no legal move loses, whether or not it is in check.
        if (LegalMoves().Count == 0)
            End(Win(Pieces.Other(SideToMove)), InCheck ? XqEndReason.Checkmate : XqEndReason.Stalemate);
        else if (XqRepetition.Judge(_hashes, _checks, _chases, _firstMover) is { } repetition)
            End(repetition.Result, repetition.Reason);
        else if (Position.QuietPlies >= NoCaptureLimit)
            End(XqResult.Draw, XqEndReason.NoCaptureLimit);
        else if (!Position.HasAttackers(XqSide.Red) && !Position.HasAttackers(XqSide.Black))
            End(XqResult.Draw, XqEndReason.NoAttackers);
    }

    /// <summary>Positions since the last capture (a capture can't be undone, so earlier ones can't repeat).</summary>
    public IReadOnlyList<ulong> RepetitionHistory()
    {
        int start = _history.Count;
        while (start > 0 && _history[start - 1].Captured == 0) start--;
        return _hashes.Skip(start).ToList();
    }

    /// <summary>
    /// Moves the side to move must avoid: ones after which it loses by a repetition rule, either at once or
    /// because the opponent can then complete the repetition (e.g. a perpetual checker can't give the check
    /// that lets the other side repeat for the third time).
    /// </summary>
    public HashSet<XqMove> MovesThatLoseByRepetition()
    {
        var losing = new HashSet<XqMove>();
        if (IsOver) return losing;
        // Only positions already seen twice can become a third occurrence.
        var seenTwice = RepetitionHistory().GroupBy(h => h).Where(g => g.Count() >= 2).Select(g => g.Key).ToHashSet();
        if (seenTwice.Count == 0) return losing;

        var me = SideToMove;
        var loss = me == XqSide.Red ? XqResult.BlackWins : XqResult.RedWins;
        var pos = Position.Clone();
        var hashes = new List<ulong>(_hashes);
        var checks = new List<bool>(_checks);
        var chases = new List<bool>(_chases);

        bool LosesNow() =>
            seenTwice.Contains(hashes[^1]) && XqRepetition.Judge(hashes, checks, chases, _firstMover) is { } r && r.Result == loss;

        void Push(XqSide mover, bool always)
        {
            hashes.Add(XqZobrist.Hash(pos));
            checks.Add(pos.InCheck(pos.SideToMove));
            // Chasing is expensive to work out: only do it when the flag can be judged.
            chases.Add((always || seenTwice.Contains(hashes[^1])) && XqRepetition.IsChasing(pos, mover));
        }
        void Pop()
        {
            hashes.RemoveAt(hashes.Count - 1);
            checks.RemoveAt(checks.Count - 1);
            chases.RemoveAt(chases.Count - 1);
        }

        foreach (var m in LegalMoves())
        {
            var undo = pos.Make(m);
            Push(me, always: true);
            bool loses = LosesNow();
            if (!loses)
            {
                foreach (var reply in pos.LegalMoves())
                {
                    var replyUndo = pos.Make(reply);
                    Push(Pieces.Other(me), always: false);
                    loses = LosesNow();
                    Pop();
                    pos.Unmake(reply, replyUndo);
                    if (loses) break;
                }
            }
            Pop();
            pos.Unmake(m, undo);
            if (loses) losing.Add(m);
        }
        return losing;
    }

    /// <summary>Pieces this side has captured, in order.</summary>
    public IEnumerable<int> CapturedBy(XqSide side) =>
        _history.Where(h => h.Captured != 0 && Pieces.SideOf(h.Piece) == side).Select(h => h.Captured);
}
