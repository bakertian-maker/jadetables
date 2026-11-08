using System.Text;

namespace GameWeb.Games.Xiangqi;

public enum XqSide { Red, Black }

/// <summary>
/// Piece codes: positive for Red, negative for Black. Type = absolute value.
/// </summary>
public static class Pieces
{
    public const int Empty = 0, General = 1, Advisor = 2, Elephant = 3, Horse = 4, Chariot = 5, Cannon = 6, Soldier = 7;

    public static int Type(int piece) => Math.Abs(piece);
    public static XqSide SideOf(int piece) => piece > 0 ? XqSide.Red : XqSide.Black;
    public static int Make(int type, XqSide side) => side == XqSide.Red ? type : -type;
    public static XqSide Other(XqSide side) => side == XqSide.Red ? XqSide.Black : XqSide.Red;

    const string FenLetters = " KABNRCP";

    public static char FenChar(int piece)
    {
        char c = FenLetters[Type(piece)];
        return piece > 0 ? c : char.ToLowerInvariant(c);
    }

    public static int FromFenChar(char c)
    {
        int type = char.ToUpperInvariant(c) switch
        {
            'K' => General, 'A' => Advisor, 'B' or 'E' => Elephant, 'N' or 'H' => Horse,
            'R' => Chariot, 'C' => Cannon, 'P' => Soldier,
            _ => throw new FormatException($"Unknown piece '{c}'"),
        };
        return char.IsUpper(c) ? type : -type;
    }
}

/// <summary>
/// Squares are 0..89: rank * 9 + file. Rank 0 is Red's back rank, rank 9 is Black's.
/// File 0 is on Red's left (file "a" in coordinate notation).
/// </summary>
public static class Sq
{
    public static int Rank(int sq) => sq / 9;
    public static int File(int sq) => sq % 9;
    public static int At(int rank, int file) => rank * 9 + file;
    public static bool OnBoard(int rank, int file) => rank is >= 0 and <= 9 && file is >= 0 and <= 8;
    public static string Name(int sq) => $"{(char)('a' + File(sq))}{Rank(sq)}";
}

public readonly record struct XqMove(int From, int To)
{
    public override string ToString() => Sq.Name(From) + Sq.Name(To);

    /// <summary>Parses coordinate notation such as "h2e2".</summary>
    public static XqMove Parse(string s)
    {
        int Square(int i) => Sq.At(s[i + 1] - '0', s[i] - 'a');
        return new XqMove(Square(0), Square(2));
    }
}

public readonly record struct XqUndo(int Captured, int QuietPlies, int FullMove);

/// <summary>A Xiangqi position with move generation and check detection. Not thread-safe; clone to share.</summary>
public sealed class XqPosition
{
    public const string StartFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    static readonly (int Dr, int Df)[] Orthogonal = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    static readonly (int Dr, int Df)[] Diagonal = [(1, 1), (1, -1), (-1, 1), (-1, -1)];
    // Horse jump (dr, df) with its leg square (lr, lf) relative to the horse.
    static readonly (int Dr, int Df, int Lr, int Lf)[] HorseJumps =
    [
        (2, 1, 1, 0), (2, -1, 1, 0), (-2, 1, -1, 0), (-2, -1, -1, 0),
        (1, 2, 0, 1), (-1, 2, 0, 1), (1, -2, 0, -1), (-1, -2, 0, -1),
    ];

    readonly int[] _board = new int[90];

    public XqSide SideToMove { get; private set; }
    /// <summary>Half-moves since the last capture (for the no-capture draw rule).</summary>
    public int QuietPlies { get; private set; }
    public int FullMove { get; private set; } = 1;

    public int this[int sq] => _board[sq];

    public XqPosition Clone()
    {
        var p = new XqPosition { SideToMove = SideToMove, QuietPlies = QuietPlies, FullMove = FullMove };
        Array.Copy(_board, p._board, 90);
        return p;
    }

    public int[] BoardCopy() => (int[])_board.Clone();

    /// <summary>A copy with a different side to move (for asking "what could this side capture here?").</summary>
    public XqPosition WithSideToMove(XqSide side)
    {
        var p = Clone();
        p.SideToMove = side;
        return p;
    }

    public static XqPosition Start() => FromFen(StartFen);

    public static XqPosition FromFen(string fen)
    {
        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ranks = parts[0].Split('/');
        if (ranks.Length != 10) throw new FormatException("FEN needs 10 ranks");
        var p = new XqPosition();
        for (int i = 0; i < 10; i++)
        {
            int rank = 9 - i, file = 0;
            foreach (var c in ranks[i])
            {
                if (char.IsDigit(c)) file += c - '0';
                else p._board[Sq.At(rank, file++)] = Pieces.FromFenChar(c);
            }
            if (file != 9) throw new FormatException($"FEN rank {i + 1} has {file} files");
        }
        p.SideToMove = parts.Length > 1 && parts[1] == "b" ? XqSide.Black : XqSide.Red;
        if (parts.Length > 4 && int.TryParse(parts[4], out var quiet)) p.QuietPlies = quiet;
        if (parts.Length > 5 && int.TryParse(parts[5], out var full)) p.FullMove = full;
        return p;
    }

    public string ToFen()
    {
        var sb = new StringBuilder();
        for (int rank = 9; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 9; file++)
            {
                int piece = _board[Sq.At(rank, file)];
                if (piece == 0) { empty++; continue; }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                sb.Append(Pieces.FenChar(piece));
            }
            if (empty > 0) sb.Append(empty);
            if (rank > 0) sb.Append('/');
        }
        sb.Append(SideToMove == XqSide.Red ? " w" : " b").Append(" - - ").Append(QuietPlies).Append(' ').Append(FullMove);
        return sb.ToString();
    }

    public XqUndo Make(XqMove m)
    {
        var undo = new XqUndo(_board[m.To], QuietPlies, FullMove);
        _board[m.To] = _board[m.From];
        _board[m.From] = 0;
        QuietPlies = undo.Captured != 0 ? 0 : QuietPlies + 1;
        if (SideToMove == XqSide.Black) FullMove++;
        SideToMove = Pieces.Other(SideToMove);
        return undo;
    }

    public void Unmake(XqMove m, XqUndo undo)
    {
        SideToMove = Pieces.Other(SideToMove);
        _board[m.From] = _board[m.To];
        _board[m.To] = undo.Captured;
        QuietPlies = undo.QuietPlies;
        FullMove = undo.FullMove;
    }

    static bool InPalace(int rank, int file, XqSide side) =>
        file is >= 3 and <= 5 && (side == XqSide.Red ? rank <= 2 : rank >= 7);

    static bool OwnHalf(int rank, XqSide side) => side == XqSide.Red ? rank <= 4 : rank >= 5;

    /// <summary>Moves that obey each piece's movement rules, ignoring whether the mover's general is left in check.</summary>
    public void GeneratePseudoLegal(List<XqMove> moves, bool capturesOnly = false)
    {
        for (int sq = 0; sq < 90; sq++)
        {
            int piece = _board[sq];
            if (piece != 0 && Pieces.SideOf(piece) == SideToMove) GenerateFrom(sq, moves, capturesOnly);
        }
    }

    void GenerateFrom(int from, List<XqMove> moves, bool capturesOnly)
    {
        int piece = _board[from];
        var side = Pieces.SideOf(piece);
        int r = Sq.Rank(from), f = Sq.File(from);

        void Add(int nr, int nf)
        {
            if (!Sq.OnBoard(nr, nf)) return;
            int to = Sq.At(nr, nf), target = _board[to];
            if (target == 0) { if (!capturesOnly) moves.Add(new XqMove(from, to)); }
            else if (Pieces.SideOf(target) != side) moves.Add(new XqMove(from, to));
        }

        switch (Pieces.Type(piece))
        {
            case Pieces.General:
                foreach (var (dr, df) in Orthogonal)
                    if (InPalace(r + dr, f + df, side)) Add(r + dr, f + df);
                break;
            case Pieces.Advisor:
                foreach (var (dr, df) in Diagonal)
                    if (InPalace(r + dr, f + df, side)) Add(r + dr, f + df);
                break;
            case Pieces.Elephant:
                foreach (var (dr, df) in Diagonal)
                {
                    int nr = r + 2 * dr, nf = f + 2 * df;
                    if (Sq.OnBoard(nr, nf) && OwnHalf(nr, side) && _board[Sq.At(r + dr, f + df)] == 0) Add(nr, nf);
                }
                break;
            case Pieces.Horse:
                foreach (var (dr, df, lr, lf) in HorseJumps)
                {
                    int nr = r + dr, nf = f + df;
                    if (Sq.OnBoard(nr, nf) && _board[Sq.At(r + lr, f + lf)] == 0) Add(nr, nf);
                }
                break;
            case Pieces.Chariot:
                foreach (var (dr, df) in Orthogonal)
                    for (int nr = r + dr, nf = f + df; Sq.OnBoard(nr, nf); nr += dr, nf += df)
                    {
                        Add(nr, nf);
                        if (_board[Sq.At(nr, nf)] != 0) break;
                    }
                break;
            case Pieces.Cannon:
                foreach (var (dr, df) in Orthogonal)
                {
                    bool screen = false;
                    for (int nr = r + dr, nf = f + df; Sq.OnBoard(nr, nf); nr += dr, nf += df)
                    {
                        int target = _board[Sq.At(nr, nf)];
                        if (!screen)
                        {
                            if (target == 0) { if (!capturesOnly) moves.Add(new XqMove(from, Sq.At(nr, nf))); }
                            else screen = true;
                        }
                        else if (target != 0)
                        {
                            if (Pieces.SideOf(target) != side) moves.Add(new XqMove(from, Sq.At(nr, nf)));
                            break;
                        }
                    }
                }
                break;
            case Pieces.Soldier:
                int forward = side == XqSide.Red ? 1 : -1;
                Add(r + forward, f);
                if (!OwnHalf(r, side))
                {
                    Add(r, f - 1);
                    Add(r, f + 1);
                }
                break;
        }
    }

    public int GeneralSquare(XqSide side)
    {
        int target = Pieces.Make(Pieces.General, side);
        int lo = side == XqSide.Red ? 0 : 7;
        for (int rank = lo; rank <= lo + 2; rank++)
            for (int file = 3; file <= 5; file++)
                if (_board[Sq.At(rank, file)] == target) return Sq.At(rank, file);
        return -1;
    }

    /// <summary>
    /// True if this side's general is attacked, including by the enemy general along an open file
    /// (the flying-general rule).
    /// </summary>
    public bool InCheck(XqSide side)
    {
        int g = GeneralSquare(side);
        if (g < 0) return true;
        var enemy = Pieces.Other(side);
        int gr = Sq.Rank(g), gf = Sq.File(g);

        // Chariots, cannons and the facing general along the four lines.
        foreach (var (dr, df) in Orthogonal)
        {
            bool screen = false;
            for (int nr = gr + dr, nf = gf + df; Sq.OnBoard(nr, nf); nr += dr, nf += df)
            {
                int piece = _board[Sq.At(nr, nf)];
                if (piece == 0) continue;
                if (!screen)
                {
                    if (Pieces.SideOf(piece) == enemy)
                    {
                        int type = Pieces.Type(piece);
                        if (type == Pieces.Chariot) return true;
                        if (type == Pieces.General && df == 0) return true;
                    }
                    screen = true;
                }
                else
                {
                    if (piece == Pieces.Make(Pieces.Cannon, enemy)) return true;
                    break;
                }
            }
        }

        // Horses: a horse at g - jump attacks g if its leg is free.
        int enemyHorse = Pieces.Make(Pieces.Horse, enemy);
        foreach (var (dr, df, lr, lf) in HorseJumps)
        {
            int hr = gr - dr, hf = gf - df;
            if (Sq.OnBoard(hr, hf) && _board[Sq.At(hr, hf)] == enemyHorse && _board[Sq.At(hr + lr, hf + lf)] == 0)
                return true;
        }

        // Soldiers: from directly in front of the general (from the soldier's point of view),
        // or beside it once the soldier has crossed the river.
        int enemySoldier = Pieces.Make(Pieces.Soldier, enemy);
        int enemyForward = enemy == XqSide.Red ? 1 : -1;
        if (Sq.OnBoard(gr - enemyForward, gf) && _board[Sq.At(gr - enemyForward, gf)] == enemySoldier) return true;
        if (!OwnHalf(gr, enemy))
        {
            if (gf > 0 && _board[Sq.At(gr, gf - 1)] == enemySoldier) return true;
            if (gf < 8 && _board[Sq.At(gr, gf + 1)] == enemySoldier) return true;
        }
        return false;
    }

    /// <summary>Moves that do not leave the mover's own general in check.</summary>
    public List<XqMove> LegalMoves()
    {
        var pseudo = new List<XqMove>(64);
        GeneratePseudoLegal(pseudo);
        var legal = new List<XqMove>(pseudo.Count);
        var mover = SideToMove;
        foreach (var m in pseudo)
        {
            var undo = Make(m);
            if (!InCheck(mover)) legal.Add(m);
            Unmake(m, undo);
        }
        return legal;
    }

    /// <summary>True if the side has any piece that can cross the river to attack (chariot, horse, cannon, soldier).</summary>
    public bool HasAttackers(XqSide side)
    {
        for (int sq = 0; sq < 90; sq++)
        {
            int piece = _board[sq];
            if (piece == 0 || Pieces.SideOf(piece) != side) continue;
            if (Pieces.Type(piece) is Pieces.Chariot or Pieces.Horse or Pieces.Cannon or Pieces.Soldier) return true;
        }
        return false;
    }

    /// <summary>Number of leaf positions after <paramref name="depth"/> plies (for move generator tests).</summary>
    public long Perft(int depth)
    {
        if (depth == 0) return 1;
        long total = 0;
        foreach (var m in LegalMoves())
        {
            var undo = Make(m);
            total += depth == 1 ? 1 : Perft(depth - 1);
            Unmake(m, undo);
        }
        return total;
    }
}
