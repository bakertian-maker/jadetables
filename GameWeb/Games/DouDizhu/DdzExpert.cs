namespace GameWeb.Games.DouDizhu;

/// <summary>
/// The Difficult computer player. Its main tool is <see cref="Turns"/>: the fewest plays a hand needs to
/// go out. Every lead or answer is judged by how many plays the rest of the hand would still need, so it
/// keeps its straights, pairs and triples together instead of breaking them up.
///
/// It also counts cards: anything not in its hand and not yet played is "still out", and a play nothing
/// still out can beat is a sure winner. With one plain play left after a sure winner, it has won.
/// </summary>
static class DdzExpert
{
    public static DdzAction Bid(DdzGame g, int seat)
    {
        var hand = g.Hands[seat];
        // A hand that goes out in few plays is worth more than its high cards alone suggest.
        int s = DdzBot.Strength(hand) + (8 - Turns(hand, new()));
        return DdzBot.BidUpTo(g, s >= 11 ? 3 : s >= 8 ? 2 : s >= 6 ? 1 : 0);
    }

    /// <summary>Random deals tried for each option in the look-ahead.</summary>
    const int Samples = 24;
    /// <summary>Options the look-ahead compares (the rule-based choice and the next best few).</summary>
    const int Shortlist = 6;

    public static DdzAction Play(DdzGame g, int seat, Random rng)
    {
        var hand = g.Hands[seat];
        var ctx = new Context(g, seat);
        var choice = g.LastCombo is null ? Lead(ctx, hand) : Follow(ctx, hand, g.LastCombo);

        // Compare the rule-based choice with the alternatives by playing each out on random deals.
        if (choice is null || choice.Cards.Count < hand.Count)
        {
            var options = new List<DdzCombo?> { choice };
            options.AddRange(g.LastCombo is null ? LeadShortlist(ctx, hand) : FollowShortlist(hand, g.LastCombo));
            options = options.DistinctBy(o => o is null ? "" : string.Join(",", o.Cards.Order())).Take(Shortlist).ToList();
            if (options.Count > 1) choice = LookAhead(g, seat, options, rng);
        }
        return choice is null ? new DdzAction(DdzActionType.Pass) : new DdzAction(DdzActionType.Play, Cards: choice.Cards);
    }

    /// <summary>
    /// Plays each option out to the end of the hand on the same <see cref="Samples"/> random deals of the
    /// unseen cards, everyone then playing at Medium level, and keeps the one that scores best. Ties go
    /// to the earlier option (the rule-based choice comes first).
    /// </summary>
    static DdzCombo? LookAhead(DdzGame g, int seat, List<DdzCombo?> options, Random rng)
    {
        var totals = new double[options.Count];
        for (int k = 0; k < Samples; k++)
        {
            int dealSeed = rng.Next();
            for (int i = 0; i < options.Count; i++)
            {
                var sim = g.Imagine(seat, new Random(dealSeed));
                var o = options[i];
                if (!sim.Apply(seat, o is null ? new DdzAction(DdzActionType.Pass) : new DdzAction(DdzActionType.Play, Cards: o.Cards)))
                {
                    totals[i] = double.MinValue;
                    continue;
                }
                for (int step = 0; step < 300 && sim.Phase == DdzPhase.Playing; step++)
                    sim.Apply(sim.CurrentSeat, DdzBot.Decide(sim, sim.CurrentSeat, DdzLevel.Medium));
                if (sim.LastResult is { } r) totals[i] += r.Changes[seat];
            }
        }
        int best = 0;
        for (int i = 1; i < options.Count; i++) if (totals[i] > totals[best]) best = i;
        return options[best];
    }

    static IEnumerable<DdzCombo?> LeadShortlist(Context ctx, List<int> hand) =>
        AllPlays(hand)
            .Select(p => (Play: p, Cost: Turns(Without(hand, p), ctx.Memo) * 100 + (p.Rank - (p.Length - 1)) * 3))
            .OrderBy(x => x.Cost)
            .Select(x => (DdzCombo?)x.Play);

    static IEnumerable<DdzCombo?> FollowShortlist(List<int> hand, DdzCombo lead)
    {
        var candidates = Combos.Beating(hand, lead);
        yield return null;   // pass
        foreach (var c in candidates.Where(c => !c.IsBomb).Take(3)) yield return c;
        if (candidates.Where(c => !c.IsBomb).LastOrDefault() is { } highest) yield return highest;
        if (candidates.FirstOrDefault(c => c.IsBomb) is { } bomb) yield return bomb;
    }

    /// <summary>What the player knows at this moment: cards still out, and how close everyone is to going out.</summary>
    sealed class Context
    {
        public Context(DdzGame g, int seat)
        {
            Game = g;
            Seat = seat;
            var known = new HashSet<int>(g.Hands[seat]);
            known.UnionWith(g.Played);
            Outside = Enumerable.Range(0, Cards.Count).Where(c => !known.Contains(c)).ToList();
            var opponents = Enumerable.Range(0, DdzGame.Players).Where(s => !g.SameSide(s, seat)).ToList();
            Danger = opponents.Min(s => g.Hands[s].Count);
            MostOpponentCards = opponents.Max(s => g.Hands[s].Count);
        }

        public DdzGame Game { get; }
        public int Seat { get; }
        public List<int> Outside { get; }
        /// <summary>The fewest cards any opponent holds.</summary>
        public int Danger { get; }
        public int MostOpponentCards { get; }
        public Dictionary<long, int> Memo { get; } = new();
        public bool IsLandlord => Game.Landlord == Seat;

        /// <summary>Could an opponent beat this play? Only with cards still out, and only with a hand big enough.</summary>
        public bool Beatable(DdzCombo play) =>
            Combos.Beating(Outside, play).Any(b => b.Cards.Count <= MostOpponentCards);
    }

    static DdzCombo Lead(Context ctx, List<int> hand)
    {
        if (Combos.Analyze(hand).FirstOrDefault() is { } all) return all;
        var g = ctx.Game;

        // A partner who plays next and is about to go out: give them something low to beat.
        if (!ctx.IsLandlord)
        {
            int next = (ctx.Seat + 1) % DdzGame.Players;
            if (next != g.Landlord)
            {
                int left = g.Hands[next].Count;
                if (left == 1) return Combos.OfKind(hand, DdzKind.Single, 1, -1).First();
                if (left == 2 && Combos.OfKind(hand, DdzKind.Pair, 1, -1).FirstOrDefault() is { } pair) return pair;
            }
        }

        DdzCombo? best = null;
        double bestCost = double.MaxValue;
        foreach (var play in AllPlays(hand))
        {
            var rest = Without(hand, play);
            int turns = Turns(rest, ctx.Memo);
            bool sure = !ctx.Beatable(play);
            int low = play.Rank - (play.Length - 1);

            double cost = turns * 100 + low * 3 - play.Cards.Count * 0.5;
            // Keep 2s, jokers and bombs to win the lead back later, unless the end is in sight.
            if (turns > 1 && (play.IsBomb || play.Rank >= Cards.RankTwo)) cost += 60;
            // A sure winner with one play left after it wins the hand.
            if (turns <= 1 && sure) cost -= 1000;
            // An opponent with one or two cards: don't lead what they can beat and finish with.
            if (!sure && ctx.Danger == 1 && play.Kind == DdzKind.Single) cost += 500 - low * 6;
            if (!sure && ctx.Danger == 2 && play.Kind == DdzKind.Pair) cost += 300;

            if (cost < bestCost) { bestCost = cost; best = play; }
        }
        return best ?? Combos.OfKind(hand, DdzKind.Single, 1, -1).First();
    }

    static DdzCombo? Follow(Context ctx, List<int> hand, DdzCombo lead)
    {
        var candidates = Combos.Beating(hand, lead);
        if (candidates.Count == 0) return null;
        var finishing = candidates.FirstOrDefault(c => c.Cards.Count == hand.Count);
        if (finishing is not null) return finishing;

        var g = ctx.Game;
        var bombRanks = DdzBot.BombRanks(hand);
        var normal = candidates.Where(c => !c.IsBomb && !c.Cards.Any(x => bombRanks.Contains(Cards.Rank(x)))).ToList();

        // Taking the lead with a sure winner and going out with the next play wins, whoever led.
        foreach (var c in candidates)
            if (!ctx.Beatable(c) && Turns(Without(hand, c), ctx.Memo) <= 1) return c;

        bool partnerWinning = g.SameSide(g.LastSeat, ctx.Seat) && g.LastSeat != ctx.Seat;
        if (partnerWinning) return null;

        // An opponent close to going out: stop them, with a sure winner if possible, else as high as we can.
        if (ctx.Danger <= 2)
        {
            var stop = normal.FirstOrDefault(c => !ctx.Beatable(c)) ?? normal.LastOrDefault();
            if (stop is not null) return stop;
            return candidates.FirstOrDefault(c => c.IsBomb);
        }

        int turnsNow = Turns(hand, ctx.Memo);
        DdzCombo? pick = null;
        int pickDamage = int.MaxValue;
        double pickCost = double.MaxValue;
        foreach (var c in normal)
        {
            // 0 when the play is one of the hand's planned plays; more when it breaks something up.
            int damage = Turns(Without(hand, c), ctx.Memo) - (turnsNow - 1);
            double cost = damage * 100 + c.Rank * 3 + (c.Rank >= Cards.RankTwo ? 40 : 0);
            if (cost < pickCost) { pickCost = cost; pick = c; pickDamage = damage; }
        }

        if (pick is not null)
        {
            bool early = hand.Count > 6 && ctx.Danger > 5;
            // Don't spend a 2 or a joker on a low card early on.
            bool wasteful = pick.Rank >= Cards.RankTwo && lead.Rank < 8 && early;
            // A farmer whose partner plays next can leave a low lead to them rather than break up the hand.
            int next = (ctx.Seat + 1) % DdzGame.Players;
            bool partnerNext = !ctx.IsLandlord && next != g.Landlord;
            if (!wasteful)
            {
                if (pickDamage <= 0) return pick;
                if (pickDamage == 1 && !(partnerNext && lead.Rank < 9) && (hand.Count <= 10 || ctx.Danger <= 5)) return pick;
            }
        }

        // Bombs: when the other side is getting close, or when the bomb nearly takes us out.
        var bomb = candidates.FirstOrDefault(c => c.IsBomb);
        if (bomb is not null && (ctx.Danger <= 4 || Turns(Without(hand, bomb), ctx.Memo) <= 1)) return bomb;
        return null;
    }

    static List<int> Without(List<int> hand, DdzCombo play)
    {
        var used = new HashSet<int>(play.Cards);
        return hand.Where(c => !used.Contains(c)).ToList();
    }

    /// <summary>Every play the hand could lead: each kind, rank and sequence length.</summary>
    static IEnumerable<DdzCombo> AllPlays(List<int> hand)
    {
        foreach (var kind in new[] { DdzKind.Single, DdzKind.Pair, DdzKind.Triple, DdzKind.TripleSingle, DdzKind.TriplePair,
                     DdzKind.FourTwoSingles, DdzKind.FourTwoPairs, DdzKind.Bomb, DdzKind.Rocket })
            foreach (var p in Combos.OfKind(hand, kind, 1, -1)) yield return p;
        for (int len = 5; len <= 12; len++)
            foreach (var p in Combos.OfKind(hand, DdzKind.Straight, len, -1)) yield return p;
        for (int len = 3; len <= 10; len++)
            foreach (var p in Combos.OfKind(hand, DdzKind.PairStraight, len, -1)) yield return p;
        foreach (var kind in new[] { DdzKind.Airplane, DdzKind.AirplaneSingles, DdzKind.AirplanePairs })
            for (int len = 2; len <= 6; len++)
                foreach (var p in Combos.OfKind(hand, kind, len, -1)) yield return p;
    }

    /// <summary>The fewest plays this hand needs to go out (an estimate: sequences are searched, the rest grouped).</summary>
    public static int Turns(IReadOnlyList<int> hand, Dictionary<long, int> memo) =>
        Turns(Cards.RankCounts(hand), memo);

    static int Turns(int[] c, Dictionary<long, int> memo)
    {
        long key = 0;
        for (int r = 0; r < Cards.RankCount; r++) key = key * 5 + c[r];
        if (memo.TryGetValue(key, out int known)) return known;

        int best = Grouped(c);
        // Try taking out each straight (5+), run of pairs (3+) and airplane (2+ triples).
        for (int per = 1; per <= 3; per++)
        {
            int minLength = per switch { 1 => 5, 2 => 3, _ => 2 };
            for (int low = 0; low <= Combos.SequenceTop; low++)
            {
                int top = low;
                while (top <= Combos.SequenceTop && c[top] >= per)
                {
                    int length = top - low + 1;
                    if (length >= minLength)
                    {
                        for (int r = low; r <= top; r++) c[r] -= per;
                        int rest = Turns(c, memo);
                        // An airplane carries one lone card or pair per triple with it.
                        if (per == 3) rest = Math.Max(0, rest - Math.Min(length, LoneGroups(c)));
                        for (int r = low; r <= top; r++) c[r] += per;
                        best = Math.Min(best, 1 + rest);
                    }
                    top++;
                }
            }
        }
        memo[key] = best;
        return best;
    }

    /// <summary>Plays needed without sequences: bombs, triples (each carrying a lone card or pair), pairs, singles.</summary>
    static int Grouped(int[] c)
    {
        bool rocket = c[Cards.RankSmallJoker] == 1 && c[Cards.RankBigJoker] == 1;
        int n1 = 0, n2 = 0, n3 = 0, n4 = 0;
        for (int r = 0; r < Cards.RankCount; r++)
        {
            if (rocket && r >= Cards.RankSmallJoker) continue;
            switch (c[r]) { case 1: n1++; break; case 2: n2++; break; case 3: n3++; break; case 4: n4++; break; }
        }
        return n4 + n3 + n2 + n1 + (rocket ? 1 : 0) - Math.Min(n3, n1 + n2);
    }

    /// <summary>Lone cards and pairs (not jokers), which triples and airplanes can carry.</summary>
    static int LoneGroups(int[] c)
    {
        int n = 0;
        for (int r = 0; r <= Cards.RankTwo; r++) if (c[r] is 1 or 2) n++;
        return n;
    }
}
