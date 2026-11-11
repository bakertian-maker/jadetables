namespace GameWeb.Games.DouDizhu;

/// <summary>
/// A rule-based computer player, solid enough for casual games:
/// <list type="bullet">
/// <item>Bids by hand strength (jokers, 2s, bombs, aces).</item>
/// <item>Leads its cheapest useful play, preferring to get rid of low cards in long sequences.</item>
/// <item>Follows with the lowest play that wins; keeps bombs for when an opponent is close to going out.</item>
/// <item>A farmer doesn't fight its partner's winning play, and plays its best when the landlord has few
/// cards left.</item>
/// </list>
/// </summary>
public static class DdzBot
{
    public static DdzAction Decide(DdzGame g, int seat)
    {
        return g.Phase switch
        {
            DdzPhase.Bidding => DecideBid(g, seat),
            DdzPhase.Playing => DecidePlay(g, seat),
            _ => new DdzAction(DdzActionType.Ready),
        };
    }

    /// <summary>A rough measure of how good a hand is for being the landlord.</summary>
    public static int Strength(IReadOnlyList<int> hand)
    {
        var c = Cards.RankCounts(hand);
        int s = 0;
        if (c[Cards.RankBigJoker] > 0) s += 4;
        if (c[Cards.RankSmallJoker] > 0) s += 3;
        if (c[Cards.RankBigJoker] > 0 && c[Cards.RankSmallJoker] > 0) s += 2;   // the rocket
        s += 2 * c[Cards.RankTwo];
        s += c[Cards.RankAce];
        for (int r = 0; r <= Cards.RankTwo; r++) if (c[r] == 4) s += 5;
        // Many lone low cards are hard to get rid of.
        int loneLow = Enumerable.Range(0, 8).Count(r => c[r] == 1);
        s -= Math.Max(0, loneLow - 3);
        return s;
    }

    static DdzAction DecideBid(DdzGame g, int seat)
    {
        int s = Strength(g.Hands[seat]);
        int want = s >= 11 ? 3 : s >= 8 ? 2 : s >= 6 ? 1 : 0;
        return want > g.Bid ? new DdzAction(DdzActionType.Bid, want) : new DdzAction(DdzActionType.Pass);
    }

    static DdzAction DecidePlay(DdzGame g, int seat)
    {
        var hand = g.Hands[seat];
        var choice = g.LastCombo is null ? ChooseLead(g, seat, hand) : ChooseFollow(g, seat, hand, g.LastCombo);
        return choice is null ? new DdzAction(DdzActionType.Pass) : new DdzAction(DdzActionType.Play, Cards: choice.Cards);
    }

    /// <summary>Opponents' smallest hand: how close the other side is to going out.</summary>
    static int DangerCards(DdzGame g, int seat) =>
        Enumerable.Range(0, DdzGame.Players).Where(s => !g.SameSide(s, seat)).Min(s => g.Hands[s].Count);

    static DdzCombo ChooseLead(DdzGame g, int seat, List<int> hand)
    {
        // Everything in one go?
        if (Combos.Analyze(hand).FirstOrDefault() is { } all) return all;

        var bombRanks = BombRanks(hand);
        var options = Combos.LeadOptions(hand)
            .Where(o => !o.Cards.Any(c => bombRanks.Contains(Cards.Rank(c))))   // don't break bombs
            .ToList();
        if (options.Count == 0) options = Combos.LeadOptions(hand);
        if (options.Count == 0) return Combos.OfKind(hand, DdzKind.Single, 1, -1).First();

        int danger = DangerCards(g, seat);
        DdzCombo best = options.OrderBy(o => LeadCost(o, danger)).First();
        // An opponent with one card left: don't hand them an easy single; lead high if a single it must be.
        if (best.Kind == DdzKind.Single && danger == 1)
        {
            var other = options.FirstOrDefault(o => o.Kind != DdzKind.Single);
            best = other ?? Combos.OfKind(hand, DdzKind.Single, 1, -1).Last();
        }
        return best;
    }

    /// <summary>Lower is better: start low, and get rid of many cards at once.</summary>
    static double LeadCost(DdzCombo o, int danger)
    {
        int low = o.Rank - (o.Length - 1);
        double cost = low * 2 - o.Cards.Count;
        if (o.Rank >= Cards.RankTwo) cost += 20;              // keep 2s and jokers for control
        if (danger <= 2 && o.Cards.Count <= danger) cost += 15; // don't lead what they can match and finish with
        return cost;
    }

    static DdzCombo? ChooseFollow(DdzGame g, int seat, List<int> hand, DdzCombo lead)
    {
        var candidates = Combos.Beating(hand, lead);
        if (candidates.Count == 0) return null;

        // Going out with this play wins the hand: always do it.
        var finishing = candidates.FirstOrDefault(c => c.Cards.Count == hand.Count);
        if (finishing is not null) return finishing;

        bool partnerWinning = g.SameSide(g.LastSeat, seat) && g.LastSeat != seat;
        int danger = DangerCards(g, seat);
        if (partnerWinning)
        {
            // Let the partner keep the trick, unless the landlord is about to go out and the partner's
            // play is weak.
            if (danger > 2 || lead.Rank >= Cards.RankAce) return null;
        }

        var bombRanks = BombRanks(hand);
        var normal = candidates.Where(c => !c.IsBomb && !c.Cards.Any(x => bombRanks.Contains(Cards.Rank(x)))).ToList();
        if (normal.Count > 0)
        {
            var pick = normal[0];   // the lowest that wins
            // Don't spend 2s or jokers on a low single or pair early on, unless the other side is close to out.
            if (pick.Rank >= Cards.RankTwo && lead.Rank < 8 && danger > 5 && hand.Count > 6) return null;
            return pick;
        }

        // Only bombs left: use one when the other side is close to winning, or when we're nearly out.
        var bomb = candidates.FirstOrDefault(c => c.IsBomb);
        if (bomb is not null && (danger <= 5 || hand.Count - bomb.Cards.Count <= 4) && !partnerWinning) return bomb;
        return null;
    }

    static HashSet<int> BombRanks(IReadOnlyList<int> hand)
    {
        var c = Cards.RankCounts(hand);
        var set = Enumerable.Range(0, Cards.RankCount).Where(r => c[r] == 4).ToHashSet();
        if (c[Cards.RankSmallJoker] == 1 && c[Cards.RankBigJoker] == 1)
        {
            set.Add(Cards.RankSmallJoker);
            set.Add(Cards.RankBigJoker);
        }
        return set;
    }
}
