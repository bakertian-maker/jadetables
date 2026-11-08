namespace GameWeb.Games.Xiangqi;

/// <summary>
/// The evaluation as a list of tunable weights: a value per piece type plus a piece-square table per type.
/// Tables have 50 entries: 10 ranks counted from the owner's side × 5 files, mirrored left-right
/// (the board is symmetric, so file a and file i share a value). A position's score is the sum over
/// pieces, Red positive, Black negative, which is linear in the weights; the tuner relies on that.
/// </summary>
public sealed class XqEval
{
    public const int Squares = 50;
    /// <summary>Weights: [0..7] piece values by type, then 8 tables of 50 (type 0 is unused).</summary>
    public const int Count = 8 + 8 * Squares;

    readonly int[] _weights;
    readonly int[] _table = new int[15 * 90]; // score for (piece + 7, square), Black already negated

    public XqEval(int[] weights)
    {
        if (weights.Length != Count) throw new ArgumentException($"Expected {Count} weights", nameof(weights));
        _weights = (int[])weights.Clone();
        for (int piece = -7; piece <= 7; piece++)
        {
            if (piece == 0) continue;
            for (int sq = 0; sq < 90; sq++)
            {
                int type = Pieces.Type(piece);
                int v = _weights[type] + _weights[PstIndex(piece, sq)];
                _table[(piece + 7) * 90 + sq] = piece > 0 ? v : -v;
            }
        }
    }

    public IReadOnlyList<int> Weights => _weights;

    public static int PstOffset(int type) => 8 + type * Squares;

    /// <summary>Index of the table weight used by this piece on this square.</summary>
    public static int PstIndex(int piece, int sq)
    {
        int file = Sq.File(sq);
        int rank = piece > 0 ? Sq.Rank(sq) : 9 - Sq.Rank(sq);
        return PstOffset(Pieces.Type(piece)) + rank * 5 + Math.Min(file, 8 - file);
    }

    /// <summary>Score from Red's point of view.</summary>
    public int ScoreForRed(XqPosition pos)
    {
        int score = 0;
        for (int sq = 0; sq < 90; sq++)
        {
            int piece = pos[sq];
            if (piece != 0) score += _table[(piece + 7) * 90 + sq];
        }
        return score;
    }

    /// <summary>Score from the side to move's point of view (what the search uses).</summary>
    public int Evaluate(XqPosition pos)
    {
        int red = ScoreForRed(pos);
        return pos.SideToMove == XqSide.Red ? red : -red;
    }

    /// <summary>
    /// The weights that are switched on in a position, with +1 for each Red piece and -1 for each Black one:
    /// ScoreForRed = sum of weight[i] * features[i].
    /// </summary>
    public static void Features(XqPosition pos, Dictionary<int, int> features)
    {
        features.Clear();
        for (int sq = 0; sq < 90; sq++)
        {
            int piece = pos[sq];
            if (piece == 0) continue;
            int sign = piece > 0 ? 1 : -1;
            int type = Pieces.Type(piece);
            if (type != Pieces.General) features[type] = features.GetValueOrDefault(type) + sign;
            int pst = PstIndex(piece, sq);
            features[pst] = features.GetValueOrDefault(pst) + sign;
        }
        foreach (var key in features.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList()) features.Remove(key);
    }

    /// <summary>The original hand-written evaluation, expressed as weights.</summary>
    public static readonly XqEval Hand = new(HandWeights());

    /// <summary>The evaluation the computer plays with: tuned weights when present, otherwise the hand-written ones.</summary>
    public static XqEval Default { get; } = XqEvalTuned.Weights is { Length: Count } tuned ? new XqEval(tuned) : Hand;

    static int[] HandWeights()
    {
        int[] values = [0, 0, 200, 200, 400, 900, 450, 100];
        var w = new int[Count];
        Array.Copy(values, w, 8);
        for (int type = 1; type <= 7; type++)
            for (int rank = 0; rank < 10; rank++)
                for (int file = 0; file < 5; file++)
                    w[PstOffset(type) + rank * 5 + file] = HandBonus(type, rank, file);
        return w;
    }

    /// <summary>The original formula's position bonus (rank from the owner's side, file 0..4 from the edge).</summary>
    static int HandBonus(int type, int rank, int file)
    {
        int centre = 4 - file;
        return type switch
        {
            Pieces.Soldier when rank >= 5 => 90 + (rank == 9 ? -40 : (rank - 5) * 12) + (centre <= 1 ? 20 : 0),
            Pieces.Soldier when rank == 4 => 10,
            Pieces.Horse => -centre * 6 + (rank is >= 2 and <= 7 ? 15 : 0) + (rank >= 5 ? 10 : 0),
            Pieces.Chariot => (rank >= 3 ? 10 : 0) + (centre == 1 ? 6 : 0),
            Pieces.Cannon => (file == 4 ? 15 : 0) + (rank == 2 ? 5 : 0),
            Pieces.General => file == 4 ? 0 : -10,
            _ => 0,
        };
    }
}
