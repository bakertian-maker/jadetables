namespace GameWeb.Games.Mahjong;

/// <summary>
/// Rule-based computer player. Discards to get closer to a ready hand (shanten, then tile acceptance),
/// and steers towards hands that reach the faan minimum: one-suit hands, all pungs, and honour pungs.
/// </summary>
public static class MahjongBot
{
    enum Plan { Open, Flush, Pungs }

    public static MjAction Decide(MahjongGame g, int seat)
    {
        switch (g.Phase)
        {
            case MjPhase.Turn when g.CurrentSeat == seat:
                return DecideTurn(g, seat);
            case MjPhase.Claim or MjPhase.RobKong:
                var o = g.GetClaimOptions(seat);
                return o is null ? new MjAction(MjActionType.Pass) : DecideClaim(g, seat, o);
            default:
                return new MjAction(MjActionType.Ready);
        }
    }

    /// <summary>The tile the bot would discard; used for the beginner hint button.</summary>
    public static int SuggestDiscard(MahjongGame g, int seat) => ChooseDiscard(g, seat);

    static MjAction DecideTurn(MahjongGame g, int seat)
    {
        var opts = g.GetTurnOptions(seat)!;
        if (opts.CanSelfDraw) return new MjAction(MjActionType.SelfDrawWin);

        var p = g.Players[seat];
        var counts = Tiles.ToCounts(p.Hand);
        if (opts.AddKongs.Count > 0) return new MjAction(MjActionType.AddKong, opts.AddKongs[0]);
        foreach (var k in opts.ConcealedKongs)
        {
            int before = BestAfterDiscard(counts, p.Melds.Count);
            counts[k] -= 4;
            int after = HandAnalyzer.Shanten(counts, p.Melds.Count + 1);
            counts[k] += 4;
            if (after <= before) return new MjAction(MjActionType.ConcealedKong, k);
        }
        return new MjAction(MjActionType.Discard, ChooseDiscard(g, seat));
    }

    static MjAction DecideClaim(MahjongGame g, int seat, ClaimOptions o)
    {
        if (o.Win) return new MjAction(MjActionType.Win);
        if (g.Phase == MjPhase.RobKong) return new MjAction(MjActionType.Pass);

        var p = g.Players[seat];
        var counts = Tiles.ToCounts(p.Hand);
        int t = o.Tile;
        int current = HandAnalyzer.Shanten(counts, p.Melds.Count);
        var plan = PickPlan(p, counts, out int flushSuit);
        bool valuable = IsValuableHonor(g, seat, t);
        bool fitsPlan = plan switch
        {
            Plan.Flush => Tiles.IsHonor(t) || t / 9 == flushSuit,
            Plan.Pungs => true,
            _ => false,
        };
        bool anyFaanRule = g.Settings.MinFaan == 0;

        if (o.Pung || o.Kong)
        {
            counts[t] -= 2;
            int after = BestAfterDiscard(counts, p.Melds.Count + 1);
            counts[t] += 2;
            bool improves = after < current;
            if (valuable || (fitsPlan && after <= current) || (anyFaanRule && improves))
            {
                if (o.Kong && plan == Plan.Pungs) return new MjAction(MjActionType.Kong);
                return new MjAction(MjActionType.Pung);
            }
        }

        if (o.Chows.Count > 0 && plan != Plan.Pungs && (anyFaanRule || (plan == Plan.Flush && t / 9 == flushSuit)))
        {
            int bestStart = -1, bestAfter = current;
            foreach (var start in o.Chows)
            {
                for (int k = start; k < start + 3; k++) if (k != t) counts[k]--;
                int after = BestAfterDiscard(counts, p.Melds.Count + 1);
                for (int k = start; k < start + 3; k++) if (k != t) counts[k]++;
                if (after < bestAfter) { bestAfter = after; bestStart = start; }
            }
            if (bestStart >= 0) return new MjAction(MjActionType.Chow, bestStart);
        }
        return new MjAction(MjActionType.Pass);
    }

    static bool IsValuableHonor(MahjongGame g, int seat, int t) =>
        Tiles.IsDragon(t) || t == Tiles.WindTile(g.SeatWind(seat)) || t == Tiles.WindTile(g.PrevailingWind);

    static Plan PickPlan(MjPlayer p, int[] counts, out int flushSuit)
    {
        var perSuit = new int[3];
        int honors = 0;
        for (int k = 0; k < Tiles.KindCount; k++)
        {
            if (Tiles.IsSuited(k)) perSuit[k / 9] += counts[k];
            else honors += counts[k];
        }
        var meldSuits = new HashSet<int>();
        bool hasChowMeld = false;
        foreach (var m in p.Melds)
        {
            if (Tiles.IsSuited(m.Tile))
            {
                perSuit[m.Tile / 9] += 3;
                meldSuits.Add(m.Tile / 9);
            }
            else honors += 3;
            if (m.Type == MeldType.Chow) hasChowMeld = true;
        }
        flushSuit = Array.IndexOf(perSuit, perSuit.Max());
        int fs = flushSuit;
        bool meldsFit = meldSuits.All(s => s == fs);
        if (meldsFit && perSuit[flushSuit] + honors >= 9) return Plan.Flush;

        int pairs = counts.Count(c => c >= 2) + p.Melds.Count(m => m.IsPungLike);
        if (!hasChowMeld && pairs >= 4) return Plan.Pungs;
        return Plan.Open;
    }

    static int BestAfterDiscard(int[] counts, int meldCount)
    {
        int best = int.MaxValue;
        for (int k = 0; k < Tiles.KindCount; k++)
        {
            if (counts[k] == 0) continue;
            counts[k]--;
            best = Math.Min(best, HandAnalyzer.Shanten(counts, meldCount));
            counts[k]++;
        }
        return best == int.MaxValue ? HandAnalyzer.Shanten(counts, meldCount) : best;
    }

    static int ChooseDiscard(MahjongGame g, int seat)
    {
        var p = g.Players[seat];
        var counts = Tiles.ToCounts(p.Hand);
        int meldCount = p.Melds.Count;
        var visible = VisibleCounts(g, seat, counts);
        var plan = PickPlan(p, counts, out int flushSuit);

        int bestTile = p.Hand[^1];
        double bestValue = double.MinValue;
        var shanten = new int[Tiles.KindCount];
        int minShanten = int.MaxValue;
        for (int k = 0; k < Tiles.KindCount; k++)
        {
            if (counts[k] == 0) continue;
            counts[k]--;
            shanten[k] = HandAnalyzer.Shanten(counts, meldCount);
            counts[k]++;
            minShanten = Math.Min(minShanten, shanten[k]);
        }

        for (int k = 0; k < Tiles.KindCount; k++)
        {
            if (counts[k] == 0) continue;
            double value = -1000 * shanten[k];
            counts[k]--;
            if (shanten[k] <= minShanten + 1)
                value += 8 * Acceptance(counts, meldCount, visible, shanten[k]);
            counts[k]++;

            if (plan == Plan.Flush && Tiles.IsSuited(k) && k / 9 != flushSuit)
                value += minShanten >= 2 ? 1400 : 700;
            if (plan == Plan.Flush && (Tiles.IsHonor(k) || k / 9 == flushSuit))
                value -= 60;
            if (Tiles.IsHonor(k))
            {
                bool valuable = IsValuableHonor(g, seat, k);
                if (valuable && counts[k] >= 2) value -= 400;
                else if (valuable) value -= 30 - 10 * visible[k];
                else if (counts[k] == 1) value += 40 + 10 * visible[k];
            }
            else if (Tiles.IsTerminal(k) && counts[k] == 1) value += 15;
            if (plan == Plan.Pungs && counts[k] >= 2) value -= 300;

            value += Random.Shared.NextDouble();
            if (value > bestValue) { bestValue = value; bestTile = k; }
        }
        return bestTile;
    }

    static int Acceptance(int[] counts, int meldCount, int[] visible, int current)
    {
        int total = 0;
        for (int k = 0; k < Tiles.KindCount; k++)
        {
            if (visible[k] >= 4 || !IsRelevant(counts, k)) continue;
            counts[k]++;
            if (HandAnalyzer.Shanten(counts, meldCount) < current) total += 4 - visible[k];
            counts[k]--;
        }
        return total;
    }

    static bool IsRelevant(int[] counts, int k)
    {
        if (counts[k] > 0) return true;
        if (Tiles.IsHonor(k)) return false;
        for (int d = -2; d <= 2; d++)
        {
            int n = k + d;
            if (d != 0 && n >= 0 && n < 27 && Tiles.SameSuit(n, k) && counts[n] > 0) return true;
        }
        return false;
    }

    static int[] VisibleCounts(MahjongGame g, int seat, int[] own)
    {
        var v = (int[])own.Clone();
        for (int s = 0; s < 4; s++)
        {
            var p = g.Players[s];
            foreach (var d in p.Discards) if (!d.Claimed) v[d.Tile]++;
            foreach (var m in p.Melds) foreach (var t in m.TilesInMeld) v[t]++;
        }
        for (int k = 0; k < v.Length; k++) v[k] = Math.Min(v[k], 4);
        return v;
    }
}
