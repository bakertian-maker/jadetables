namespace GameWeb.Games.Xiangqi;

/// <summary>
/// Zobrist hashing: a 64-bit key per (piece, square), XOR-ed together, plus a key for Black to move.
/// The opening book in the database is keyed by these hashes, so the keys must never change:
/// they come from a fixed SplitMix64 sequence, not from System.Random. A unit test pins the
/// start position's hash.
/// </summary>
public static class XqZobrist
{
    const ulong Seed = 0x5851F42D4C957F2DUL;

    static readonly ulong[] Keys = BuildKeys();
    public static readonly ulong BlackToMove = Keys[^1];

    static ulong[] BuildKeys()
    {
        // 15 piece codes (-7..7, index 0 unused) x 90 squares, then one side-to-move key.
        var keys = new ulong[15 * 90 + 1];
        ulong state = Seed;
        for (int i = 0; i < keys.Length; i++) keys[i] = SplitMix64(ref state);
        return keys;
    }

    static ulong SplitMix64(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static ulong Key(int piece, int sq) => Keys[(piece + 7) * 90 + sq];

    public static ulong Hash(XqPosition pos)
    {
        ulong h = pos.SideToMove == XqSide.Black ? BlackToMove : 0;
        for (int sq = 0; sq < 90; sq++)
            if (pos[sq] != 0) h ^= Key(pos[sq], sq);
        return h;
    }

    /// <summary>The hash after making <paramref name="m"/> in <paramref name="pos"/> (call before making it).</summary>
    public static ulong After(ulong hash, XqPosition pos, XqMove m)
    {
        int piece = pos[m.From], captured = pos[m.To];
        hash ^= Key(piece, m.From) ^ Key(piece, m.To) ^ BlackToMove;
        if (captured != 0) hash ^= Key(captured, m.To);
        return hash;
    }
}
