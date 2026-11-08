namespace GameWeb.Games.Xiangqi;

/// <summary>
/// Repetition rules (the simplified form used by most online Xiangqi play). When a position occurs for
/// the third time with the same side to move, look at the moves of the last cycle:
/// <list type="number">
/// <item>One side checked with every move and the other didn't: the checking side loses (长将判负).</item>
/// <item>Otherwise, one side chased with every move and the other didn't: the chasing side loses (长捉判负).</item>
/// <item>Otherwise: a draw (重复局面判和).</item>
/// </list>
/// A "chase" is a threat to capture an enemy piece that is unprotected, or worth more than the attacker
/// (a horse or cannon attacking a chariot). The general and soldiers may chase freely, and a soldier
/// that hasn't crossed the river can't be chased. The full Asian (AXF) rules have many more special
/// cases; these cover what happens in practice.
/// </summary>
public static class XqRepetition
{
    /// <summary>Piece values used to decide "worth more than the attacker".</summary>
    static readonly int[] ChaseValue = [0, 0, 200, 200, 400, 900, 450, 100];

    /// <summary>After <paramref name="mover"/> has moved (the position <paramref name="after"/>), does it chase an enemy piece?</summary>
    public static bool IsChasing(XqPosition after, XqSide mover)
    {
        var p = after.WithSideToMove(mover);
        var captures = new List<XqMove>(16);
        p.GeneratePseudoLegal(captures, capturesOnly: true);
        foreach (var c in captures)
        {
            int attacker = Pieces.Type(p[c.From]);
            int target = Pieces.Type(p[c.To]);
            if (attacker is Pieces.General or Pieces.Soldier) continue;   // these may chase freely
            if (target == Pieces.General) continue;                      // that's check, not chase
            if (target == Pieces.Soldier && !HasCrossedRiver(c.To, Pieces.SideOf(p[c.To]))) continue;

            var undo = p.Make(c);
            bool legal = !p.InCheck(mover);
            bool defended = legal && p.LegalMoves().Any(m => m.To == c.To);
            p.Unmake(c, undo);
            if (legal && (!defended || ChaseValue[attacker] < ChaseValue[target])) return true;
        }
        return false;
    }

    static bool HasCrossedRiver(int sq, XqSide side) => side == XqSide.Red ? Sq.Rank(sq) >= 5 : Sq.Rank(sq) <= 4;

    /// <summary>
    /// Judges a repetition. <paramref name="hashes"/> holds the position after each half-move (index 0 is
    /// the starting position); move j turns hashes[j] into hashes[j + 1], and checks[j] / chases[j] say
    /// whether that move gave check / chased. Returns null unless the last position is a third occurrence.
    /// </summary>
    public static (XqResult Result, XqEndReason Reason)? Judge(
        IReadOnlyList<ulong> hashes, IReadOnlyList<bool> checks, IReadOnlyList<bool> chases, XqSide firstMover)
    {
        int last = hashes.Count - 1;
        ulong current = hashes[last];
        int occurrences = 1, previous = -1;
        for (int i = last - 1; i >= 0; i--)
        {
            if (hashes[i] != current) continue;
            occurrences++;
            if (previous < 0) previous = i;
        }
        if (occurrences < 3) return null;

        // The last cycle: moves previous .. last - 1.
        bool IsRed(int move) => (move % 2 == 0) == (firstMover == XqSide.Red);
        bool Every(IReadOnlyList<bool> flags, bool red)
        {
            bool any = false;
            for (int j = previous; j < last; j++)
            {
                if (IsRed(j) != red) continue;
                any = true;
                if (!flags[j]) return false;
            }
            return any;
        }

        bool redChecks = Every(checks, red: true), blackChecks = Every(checks, red: false);
        if (redChecks != blackChecks)
            return (redChecks ? XqResult.BlackWins : XqResult.RedWins, XqEndReason.PerpetualCheck);
        if (!redChecks)
        {
            bool redChases = Every(chases, red: true), blackChases = Every(chases, red: false);
            if (redChases != blackChases)
                return (redChases ? XqResult.BlackWins : XqResult.RedWins, XqEndReason.PerpetualChase);
        }
        return (XqResult.Draw, XqEndReason.Repetition);
    }
}
