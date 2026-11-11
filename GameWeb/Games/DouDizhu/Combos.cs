namespace GameWeb.Games.DouDizhu;

/// <summary>The kinds of play in Dou Dizhu.</summary>
public enum DdzKind : byte
{
    Single, Pair, Triple,
    /// <summary>Three of a kind with one card (三带一).</summary>
    TripleSingle,
    /// <summary>Three of a kind with a pair (三带二).</summary>
    TriplePair,
    /// <summary>Five or more singles in a row, 3 up to A (顺子).</summary>
    Straight,
    /// <summary>Three or more pairs in a row (连对).</summary>
    PairStraight,
    /// <summary>Two or more triples in a row (飞机).</summary>
    Airplane,
    /// <summary>An airplane with one extra card per triple (飞机带单).</summary>
    AirplaneSingles,
    /// <summary>An airplane with one extra pair per triple (飞机带对).</summary>
    AirplanePairs,
    /// <summary>Four of a kind with two cards (四带二).</summary>
    FourTwoSingles,
    /// <summary>Four of a kind with two pairs (四带两对).</summary>
    FourTwoPairs,
    /// <summary>Four of a kind on its own (炸弹): beats anything except a bigger bomb or the rocket.</summary>
    Bomb,
    /// <summary>Both jokers (王炸): beats everything.</summary>
    Rocket,
}

/// <summary>
/// A legal play. <paramref name="Rank"/> is what decides who is higher: the rank of the single, pair,
/// triple or four, or the top rank of a sequence. <paramref name="Length"/> is how many units a
/// sequence has (cards in a straight, pairs, triples); 1 for everything else. Only plays of the same kind
/// and length can beat each other, apart from bombs and the rocket.
/// </summary>
public sealed record DdzCombo(DdzKind Kind, int Rank, int Length, IReadOnlyList<int> Cards)
{
    public bool IsBomb => Kind is DdzKind.Bomb or DdzKind.Rocket;

    /// <summary>True if this play beats <paramref name="lead"/>.</summary>
    public bool Beats(DdzCombo lead)
    {
        if (lead.Kind == DdzKind.Rocket) return false;
        if (Kind == DdzKind.Rocket) return true;
        if (Kind == DdzKind.Bomb) return lead.Kind != DdzKind.Bomb || Rank > lead.Rank;
        if (lead.Kind == DdzKind.Bomb) return false;
        return Kind == lead.Kind && Length == lead.Length && Rank > lead.Rank;
    }
}

/// <summary>Recognising plays, and finding plays in a hand (for hints and the computer).</summary>
public static class Combos
{
    /// <summary>Highest rank a sequence may reach: A (2 and the jokers never join a sequence).</summary>
    const int SequenceTop = Cards.RankAce;

    /// <summary>
    /// Every way the cards can be read as a play, best reading first (e.g. 333444555666 is a four-triple
    /// airplane, or three triples carrying 6, 6, 6). Empty if the cards aren't a legal play.
    /// </summary>
    public static List<DdzCombo> Analyze(IReadOnlyList<int> cards)
    {
        var result = new List<DdzCombo>();
        int n = cards.Count;
        if (n == 0) return result;
        var c = Cards.RankCounts(cards);
        var ranks = Enumerable.Range(0, Cards.RankCount).Where(r => c[r] > 0).ToList();
        void Add(DdzKind k, int rank, int length = 1) => result.Add(new DdzCombo(k, rank, length, cards.ToList()));

        if (n == 2 && c[Cards.RankSmallJoker] == 1 && c[Cards.RankBigJoker] == 1) { Add(DdzKind.Rocket, Cards.RankBigJoker); return result; }
        if (n == 4 && ranks.Count == 1) { Add(DdzKind.Bomb, ranks[0]); return result; }
        if (n == 1) { Add(DdzKind.Single, ranks[0]); return result; }
        if (n == 2 && ranks.Count == 1 && ranks[0] <= Cards.RankTwo) { Add(DdzKind.Pair, ranks[0]); return result; }
        if (n == 3 && ranks.Count == 1) { Add(DdzKind.Triple, ranks[0]); return result; }

        // Pure sequences.
        if (IsRun(ranks) && ranks[^1] <= SequenceTop)
        {
            if (n >= 5 && ranks.All(r => c[r] == 1)) Add(DdzKind.Straight, ranks[^1], n);
            if (n >= 6 && ranks.All(r => c[r] == 2)) Add(DdzKind.PairStraight, ranks[^1], n / 2);
            if (n >= 6 && ranks.All(r => c[r] == 3)) Add(DdzKind.Airplane, ranks[^1], n / 3);
        }

        // Three with one / with a pair.
        if (n == 4 && ranks.Count == 2 && ranks.Any(r => c[r] == 3))
            Add(DdzKind.TripleSingle, ranks.First(r => c[r] == 3));
        if (n == 5 && ranks.Count == 2 && ranks.Any(r => c[r] == 3) && ranks.Any(r => c[r] == 2 && r <= Cards.RankTwo))
            Add(DdzKind.TriplePair, ranks.First(r => c[r] == 3));

        // Four with two singles / two pairs.
        foreach (var q in ranks.Where(r => c[r] == 4))
        {
            var rest = new int[Cards.RankCount];
            Array.Copy(c, rest, rest.Length);
            rest[q] = 0;
            if (n == 6 && !IsRocketPair(rest)) Add(DdzKind.FourTwoSingles, q);
            if (n == 8 && PairsOnly(rest, 2)) Add(DdzKind.FourTwoPairs, q);
        }

        // Airplanes with wings: k triples in a row plus k singles (4k cards) or k pairs (5k cards).
        foreach (var (wings, size) in new[] { (DdzKind.AirplaneSingles, 4), (DdzKind.AirplanePairs, 5) })
        {
            if (n % size != 0 || n / size < 2) continue;
            int k = n / size;
            for (int top = SequenceTop; top - k + 1 >= 0; top--)
            {
                int low = top - k + 1;
                if (!Enumerable.Range(low, k).All(r => c[r] >= 3)) continue;
                var rest = new int[Cards.RankCount];
                Array.Copy(c, rest, rest.Length);
                for (int r = low; r <= top; r++) rest[r] -= 3;
                bool ok = wings == DdzKind.AirplaneSingles ? rest.Sum() == k && !IsRocketPair(rest) : PairsOnly(rest, k);
                if (ok) { Add(wings, top, k); break; }   // the highest airplane in the cards
            }
        }
        return result;
    }

    /// <summary>The reading of <paramref name="cards"/> that beats <paramref name="lead"/> (or the first reading when leading).</summary>
    public static DdzCombo? Read(IReadOnlyList<int> cards, DdzCombo? lead)
    {
        var readings = Analyze(cards);
        return lead is null ? readings.FirstOrDefault() : readings.FirstOrDefault(r => r.Beats(lead));
    }

    static bool IsRun(List<int> ranks) => ranks.Count > 0 && ranks[^1] - ranks[0] == ranks.Count - 1;

    static bool IsRocketPair(int[] counts) => counts[Cards.RankSmallJoker] > 0 && counts[Cards.RankBigJoker] > 0;

    /// <summary>The counts make exactly <paramref name="pairs"/> pairs (a four counts as two), with no jokers.</summary>
    static bool PairsOnly(int[] counts, int pairs)
    {
        int total = 0;
        for (int r = 0; r < Cards.RankCount; r++)
        {
            if (counts[r] == 0) continue;
            if (r > Cards.RankTwo || counts[r] % 2 != 0) return false;
            total += counts[r] / 2;
        }
        return total == pairs;
    }

    // ------------------------------------------------------------------ finding plays in a hand

    /// <summary>
    /// Plays from <paramref name="hand"/> that beat <paramref name="lead"/>: same kind first (lowest first),
    /// then bombs, then the rocket. Each candidate uses the cheapest cards (low kickers, unbroken bombs).
    /// </summary>
    public static List<DdzCombo> Beating(IReadOnlyList<int> hand, DdzCombo lead)
    {
        var list = new List<DdzCombo>();
        if (!lead.IsBomb) list.AddRange(OfKind(hand, lead.Kind, lead.Length, lead.Rank));
        list.AddRange(OfKind(hand, DdzKind.Bomb, 1, lead.Kind == DdzKind.Bomb ? lead.Rank : -1));
        list.AddRange(OfKind(hand, DdzKind.Rocket, 1, -1));
        return list;
    }

    /// <summary>All plays of one kind and length in the hand with a rank above <paramref name="above"/>, lowest first.</summary>
    public static IEnumerable<DdzCombo> OfKind(IReadOnlyList<int> hand, DdzKind kind, int length, int above)
    {
        var byRank = Enumerable.Range(0, Cards.RankCount).Select(_ => new List<int>()).ToArray();
        foreach (var card in hand.OrderBy(x => x)) byRank[Cards.Rank(card)].Add(card);
        int Count(int r) => byRank[r].Count;
        List<int> Take(int r, int k) => byRank[r].Take(k).ToList();

        switch (kind)
        {
            case DdzKind.Single:
                for (int r = above + 1; r < Cards.RankCount; r++)
                    if (Count(r) >= 1) yield return new DdzCombo(kind, r, 1, Take(r, 1));
                break;
            case DdzKind.Pair:
            case DdzKind.Triple:
            {
                int k = kind == DdzKind.Pair ? 2 : 3;
                for (int r = above + 1; r <= Cards.RankTwo; r++)
                    if (Count(r) >= k) yield return new DdzCombo(kind, r, 1, Take(r, k));
                break;
            }
            case DdzKind.TripleSingle:
            case DdzKind.TriplePair:
            {
                bool pair = kind == DdzKind.TriplePair;
                for (int r = above + 1; r <= Cards.RankTwo; r++)
                {
                    if (Count(r) < 3) continue;
                    var kicker = Kickers(byRank, [r], 1, pair);
                    if (kicker is null) continue;
                    yield return new DdzCombo(kind, r, 1, [.. Take(r, 3), .. kicker]);
                }
                break;
            }
            case DdzKind.Straight:
            case DdzKind.PairStraight:
            case DdzKind.Airplane:
            case DdzKind.AirplaneSingles:
            case DdzKind.AirplanePairs:
            {
                int per = kind switch { DdzKind.Straight => 1, DdzKind.PairStraight => 2, _ => 3 };
                for (int top = Math.Max(above + 1, length - 1); top <= SequenceTop; top++)
                {
                    int low = top - length + 1;
                    if (!Enumerable.Range(low, length).All(r => Count(r) >= per)) continue;
                    var cards = Enumerable.Range(low, length).SelectMany(r => Take(r, per)).ToList();
                    if (kind is DdzKind.AirplaneSingles or DdzKind.AirplanePairs)
                    {
                        var wings = Kickers(byRank, [.. Enumerable.Range(low, length)], length, kind == DdzKind.AirplanePairs);
                        if (wings is null) continue;
                        cards.AddRange(wings);
                    }
                    yield return new DdzCombo(kind, top, length, cards);
                }
                break;
            }
            case DdzKind.FourTwoSingles:
            case DdzKind.FourTwoPairs:
                for (int r = above + 1; r <= Cards.RankTwo; r++)
                {
                    if (Count(r) != 4) continue;
                    var kicker = Kickers(byRank, [r], 2, kind == DdzKind.FourTwoPairs);
                    if (kicker is null) continue;
                    yield return new DdzCombo(kind, r, 1, [.. Take(r, 4), .. kicker]);
                }
                break;
            case DdzKind.Bomb:
                for (int r = above + 1; r <= Cards.RankTwo; r++)
                    if (Count(r) == 4) yield return new DdzCombo(kind, r, 1, Take(r, 4));
                break;
            case DdzKind.Rocket:
                if (Count(Cards.RankSmallJoker) == 1 && Count(Cards.RankBigJoker) == 1)
                    yield return new DdzCombo(kind, Cards.RankBigJoker, 1, [Cards.SmallJoker, Cards.BigJoker]);
                break;
        }
    }

    /// <summary>
    /// The cheapest <paramref name="count"/> kicker cards (or pairs) from ranks not in <paramref name="exclude"/>:
    /// lone cards before breaking pairs or triples, never breaking a bomb or using both jokers if it can be
    /// helped. Null if the hand can't supply them.
    /// </summary>
    static List<int>? Kickers(List<int>[] byRank, int[] exclude, int count, bool pairs)
    {
        var options = new List<(int Rank, int Cost)>();
        for (int r = 0; r < Cards.RankCount; r++)
        {
            int have = byRank[r].Count;
            if (exclude.Contains(r) || have == 0) continue;
            if (pairs && (have < 2 || r > Cards.RankTwo)) continue;
            // Cheaper: exactly the size needed, lower ranks; dearer: breaking bigger groups, 2s and jokers.
            int need = pairs ? 2 : 1;
            int cost = (have == need ? 0 : have == 4 ? 300 : 100 * (have - need)) + r * 2 + (r >= Cards.RankTwo ? 60 : 0);
            options.Add((r, cost));
        }
        var picked = new List<int>();
        bool usedSmallJoker = false;
        foreach (var (r, _) in options.OrderBy(o => o.Cost))
        {
            if (picked.Count >= count * (pairs ? 2 : 1)) break;
            if (!pairs && r == Cards.RankBigJoker && usedSmallJoker) continue;   // not both jokers as wings
            int take = pairs ? 2 : 1;
            picked.AddRange(byRank[r].Take(take));
            if (r == Cards.RankSmallJoker) usedSmallJoker = true;
        }
        return picked.Count == count * (pairs ? 2 : 1) ? picked : null;
    }

    /// <summary>
    /// Plays the hand could lead with, one of each useful shape: the lowest of each kind, longest sequences
    /// first. Used by the computer when it starts a trick.
    /// </summary>
    public static List<DdzCombo> LeadOptions(IReadOnlyList<int> hand)
    {
        var options = new List<DdzCombo>();
        foreach (var kind in new[] { DdzKind.Airplane, DdzKind.AirplanePairs, DdzKind.AirplaneSingles })
            for (int len = 6; len >= 2; len--)
                if (OfKind(hand, kind, len, -1).FirstOrDefault() is { } a) { options.Add(a); break; }
        for (int len = 10; len >= 3; len--)
            if (OfKind(hand, DdzKind.PairStraight, len, -1).FirstOrDefault() is { } p) { options.Add(p); break; }
        for (int len = 12; len >= 5; len--)
            if (OfKind(hand, DdzKind.Straight, len, -1).FirstOrDefault() is { } s) { options.Add(s); break; }
        foreach (var kind in new[] { DdzKind.TriplePair, DdzKind.TripleSingle, DdzKind.Triple, DdzKind.Pair, DdzKind.Single })
            if (OfKind(hand, kind, 1, -1).FirstOrDefault() is { } x) options.Add(x);
        return options;
    }
}
