namespace GameWeb.Games.Mahjong;

public enum MeldType { Chow, Pung, Kong }

/// <summary>An exposed (or concealed-kong) meld on the table.</summary>
public sealed record Meld(MeldType Type, int Tile, bool Concealed, int ClaimedTile = -1, int FromSeat = -1)
{
    public IEnumerable<int> TilesInMeld => Type switch
    {
        MeldType.Chow => [Tile, Tile + 1, Tile + 2],
        MeldType.Pung => [Tile, Tile, Tile],
        _ => [Tile, Tile, Tile, Tile],
    };

    public bool IsPungLike => Type != MeldType.Chow;
}

public readonly record struct SetInfo(MeldType Type, int Tile, bool Concealed);

public sealed record Decomposition(int Pair, IReadOnlyList<SetInfo> Sets);

public static class HandAnalyzer
{
    /// <summary>All ways to split concealed tiles into sets plus one pair. Counts must total 3n+2.</summary>
    public static List<Decomposition> Decompose(int[] counts)
    {
        var results = new List<Decomposition>();
        if (counts.Sum() % 3 != 2) return results;
        var c = (int[])counts.Clone();
        var sets = new List<SetInfo>();
        for (int p = 0; p < Tiles.KindCount; p++)
        {
            if (c[p] < 2) continue;
            c[p] -= 2;
            Search(c, 0, sets, p, results);
            c[p] += 2;
        }
        return results;
    }

    static void Search(int[] c, int i, List<SetInfo> sets, int pair, List<Decomposition> results)
    {
        while (i < Tiles.KindCount && c[i] == 0) i++;
        if (i == Tiles.KindCount)
        {
            results.Add(new Decomposition(pair, sets.ToArray()));
            return;
        }
        if (c[i] >= 3)
        {
            c[i] -= 3;
            sets.Add(new SetInfo(MeldType.Pung, i, true));
            Search(c, i, sets, pair, results);
            sets.RemoveAt(sets.Count - 1);
            c[i] += 3;
        }
        if (i < 27 && i % 9 <= 6 && c[i + 1] > 0 && c[i + 2] > 0)
        {
            c[i]--; c[i + 1]--; c[i + 2]--;
            sets.Add(new SetInfo(MeldType.Chow, i, true));
            Search(c, i, sets, pair, results);
            sets.RemoveAt(sets.Count - 1);
            c[i]++; c[i + 1]++; c[i + 2]++;
        }
    }

    public static bool IsThirteenOrphans(int[] c)
    {
        if (c.Sum() != 14) return false;
        int pairs = 0;
        foreach (var o in Tiles.Orphans)
        {
            if (c[o] == 0) return false;
            if (c[o] == 2) pairs++;
        }
        int orphanTotal = Tiles.Orphans.Sum(o => c[o]);
        return orphanTotal == 14 && pairs == 1;
    }

    public static bool IsComplete(int[] counts, int meldCount) =>
        (meldCount == 0 && IsThirteenOrphans(counts)) || Decompose(counts).Count > 0;

    /// <summary>
    /// Shanten number for a standard hand: tiles away from ready (0 = ready, -1 = complete).
    /// Counts should hold 13 - 3*meldCount (or one more) concealed tiles.
    /// </summary>
    public static int Shanten(int[] counts, int meldCount)
    {
        int need = 4 - meldCount;
        // Combine per-group tables: best[p][m] = most partial sets with m sets and p pairs.
        var acc = new int[2, 5];
        for (int p = 0; p < 2; p++) for (int m = 0; m < 5; m++) acc[p, m] = -1;
        acc[0, 0] = 0;
        for (int g = 0; g < 4; g++)
        {
            var table = GroupTable(counts, g);
            var next = new int[2, 5];
            for (int p = 0; p < 2; p++) for (int m = 0; m < 5; m++) next[p, m] = -1;
            for (int p1 = 0; p1 < 2; p1++)
                for (int m1 = 0; m1 < 5; m1++)
                {
                    if (acc[p1, m1] < 0) continue;
                    for (int p2 = 0; p1 + p2 < 2; p2++)
                        for (int m2 = 0; m1 + m2 < 5; m2++)
                        {
                            int t2 = table[p2 * 5 + m2];
                            if (t2 < 0) continue;
                            int t = acc[p1, m1] + t2;
                            if (t > next[p1 + p2, m1 + m2]) next[p1 + p2, m1 + m2] = t;
                        }
                }
            acc = next;
        }
        int best = 2 * need;
        for (int p = 0; p < 2; p++)
            for (int m = 0; m <= need; m++)
            {
                if (acc[p, m] < 0) continue;
                int s = 2 * need - 2 * m - Math.Min(acc[p, m], need - m) - p;
                if (s < best) best = s;
            }
        return Math.Min(best, OrphansShanten(counts, meldCount));
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, sbyte[]> GroupCache = new();

    /// <summary>For one suit (g = 0..2) or the honours (g = 3): max partial sets per (pair, sets).</summary>
    static sbyte[] GroupTable(int[] counts, int g)
    {
        int start = g * 9, len = g == 3 ? 7 : 9;
        int key = g == 3 ? 1 : 0;
        for (int i = 0; i < len; i++) key = key * 5 + counts[start + i];
        key = key * 2 + (g == 3 ? 1 : 0);
        return GroupCache.GetOrAdd(key, _ =>
        {
            var c = new int[len];
            Array.Copy(counts, start, c, 0, len);
            var table = new sbyte[10];
            Array.Fill(table, (sbyte)-1);
            GroupWalk(c, 0, 0, 0, 0, g == 3, table);
            return table;
        });
    }

    static void GroupWalk(int[] c, int i, int m, int t, int p, bool honors, sbyte[] table)
    {
        while (i < c.Length && c[i] == 0) i++;
        if (i == c.Length)
        {
            int idx = p * 5 + Math.Min(m, 4);
            if (t > table[idx]) table[idx] = (sbyte)Math.Min(t, 8);
            return;
        }
        if (c[i] >= 3) { c[i] -= 3; GroupWalk(c, i, m + 1, t, p, honors, table); c[i] += 3; }
        if (!honors && i <= 6 && c[i + 1] > 0 && c[i + 2] > 0)
        {
            c[i]--; c[i + 1]--; c[i + 2]--;
            GroupWalk(c, i, m + 1, t, p, honors, table);
            c[i]++; c[i + 1]++; c[i + 2]++;
        }
        if (c[i] >= 2)
        {
            c[i] -= 2;
            if (p == 0) GroupWalk(c, i, m, t, 1, honors, table);
            GroupWalk(c, i, m, t + 1, p, honors, table);
            c[i] += 2;
        }
        if (!honors && i <= 7 && c[i + 1] > 0)
        {
            c[i]--; c[i + 1]--; GroupWalk(c, i, m, t + 1, p, honors, table); c[i]++; c[i + 1]++;
        }
        if (!honors && i <= 6 && c[i + 2] > 0)
        {
            c[i]--; c[i + 2]--; GroupWalk(c, i, m, t + 1, p, honors, table); c[i]++; c[i + 2]++;
        }
        c[i]--; GroupWalk(c, i, m, t, p, honors, table); c[i]++;
    }

    static int OrphansShanten(int[] counts, int meldCount)
    {
        if (meldCount != 0) return int.MaxValue;
        int distinct = 0; bool pair = false;
        foreach (var o in Tiles.Orphans)
        {
            if (counts[o] > 0) distinct++;
            if (counts[o] > 1) pair = true;
        }
        return 13 - distinct - (pair ? 1 : 0);
    }

    /// <summary>Straightforward whole-hand search; kept to cross-check the fast version in tests.</summary>
    internal static int ShantenReference(int[] counts, int meldCount)
    {
        int need = 4 - meldCount;
        var c = (int[])counts.Clone();
        int best = 2 * need;
        Walk(c, 0, 0, 0, false, need, ref best);
        if (meldCount == 0)
        {
            int distinct = 0; bool pair = false;
            foreach (var o in Tiles.Orphans)
            {
                if (counts[o] > 0) distinct++;
                if (counts[o] > 1) pair = true;
            }
            best = Math.Min(best, 13 - distinct - (pair ? 1 : 0));
        }
        return best;
    }

    static void Walk(int[] c, int i, int m, int t, bool pair, int need, ref int best)
    {
        while (i < Tiles.KindCount && c[i] == 0) i++;
        if (i == Tiles.KindCount)
        {
            int tt = Math.Min(t, need - m);
            int s = 2 * need - 2 * m - tt - (pair ? 1 : 0);
            if (s < best) best = s;
            return;
        }
        if (best == -1) return;

        if (m < need)
        {
            if (c[i] >= 3)
            {
                c[i] -= 3; Walk(c, i, m + 1, t, pair, need, ref best); c[i] += 3;
            }
            if (i < 27 && i % 9 <= 6 && c[i + 1] > 0 && c[i + 2] > 0)
            {
                c[i]--; c[i + 1]--; c[i + 2]--;
                Walk(c, i, m + 1, t, pair, need, ref best);
                c[i]++; c[i + 1]++; c[i + 2]++;
            }
        }
        if (!pair && c[i] >= 2)
        {
            c[i] -= 2; Walk(c, i, m, t, true, need, ref best); c[i] += 2;
        }
        if (m + t < need)
        {
            if (c[i] >= 2)
            {
                c[i] -= 2; Walk(c, i, m, t + 1, pair, need, ref best); c[i] += 2;
            }
            if (i < 27 && i % 9 <= 7 && c[i + 1] > 0)
            {
                c[i]--; c[i + 1]--; Walk(c, i, m, t + 1, pair, need, ref best); c[i]++; c[i + 1]++;
            }
            if (i < 27 && i % 9 <= 6 && c[i + 2] > 0)
            {
                c[i]--; c[i + 2]--; Walk(c, i, m, t + 1, pair, need, ref best); c[i]++; c[i + 2]++;
            }
        }
        // Leave this tile isolated.
        c[i]--; Walk(c, i, m, t, pair, need, ref best); c[i]++;
    }
}
