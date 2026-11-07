namespace GameWeb.Games.Xiangqi;

public enum XqResult { Ongoing, RedWins, BlackWins, Draw }

public enum XqEndReason { None, Checkmate, Stalemate, Resignation, Timeout, NoCaptureLimit, NoAttackers, Agreement }

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
    List<XqMove>? _legal;

    public XqGame(string? fen = null)
    {
        Position = fen is null ? XqPosition.Start() : XqPosition.FromFen(fen);
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
        var undo = Position.Make(m);
        _history.Add(new XqMoveRecord(m, piece, undo.Captured, notation, Position.InCheck(Position.SideToMove)));
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
        else if (Position.QuietPlies >= NoCaptureLimit)
            End(XqResult.Draw, XqEndReason.NoCaptureLimit);
        else if (!Position.HasAttackers(XqSide.Red) && !Position.HasAttackers(XqSide.Black))
            End(XqResult.Draw, XqEndReason.NoAttackers);
    }

    /// <summary>Pieces this side has captured, in order.</summary>
    public IEnumerable<int> CapturedBy(XqSide side) =>
        _history.Where(h => h.Captured != 0 && Pieces.SideOf(h.Piece) == side).Select(h => h.Captured);
}
