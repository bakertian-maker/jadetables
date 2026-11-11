namespace GameWeb.Games.DouDizhu;

/// <summary>
/// The 54-card Dou Dizhu deck. A card is an int: 0–51 are the suited cards, rank × 4 + suit, and
/// 52 / 53 are the small and big jokers. Ranks run from 3 (lowest) to 2 (highest), then the jokers:
/// 0 = 3, 1 = 4, … 7 = 10, 8 = J, 9 = Q, 10 = K, 11 = A, 12 = 2, 13 = small joker, 14 = big joker.
/// </summary>
public static class Cards
{
    public const int Count = 54;
    public const int SmallJoker = 52;
    public const int BigJoker = 53;

    public const int RankAce = 11;
    public const int RankTwo = 12;
    public const int RankSmallJoker = 13;
    public const int RankBigJoker = 14;
    public const int RankCount = 15;

    static readonly string[] RankLabels = ["3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A", "2", "", ""];
    static readonly string[] SuitSymbols = ["♠", "♥", "♣", "♦"];

    public static int Rank(int card) => card < 52 ? card / 4 : RankSmallJoker + (card - 52);

    /// <summary>0 ♠, 1 ♥, 2 ♣, 3 ♦; −1 for jokers.</summary>
    public static int Suit(int card) => card < 52 ? card % 4 : -1;

    public static bool IsJoker(int card) => card >= 52;

    public static bool IsRed(int card) => card == BigJoker || Suit(card) is 1 or 3;

    /// <summary>"10", "J", "A", "2"; empty for jokers (the UI writes those out).</summary>
    public static string RankLabel(int card) => RankLabels[Rank(card)];

    public static string RankLabelOf(int rank) => rank < RankLabels.Length ? RankLabels[rank] : "";

    public static string SuitSymbol(int card) => card < 52 ? SuitSymbols[Suit(card)] : "";

    /// <summary>A shuffled deck.</summary>
    public static List<int> ShuffledDeck(Random rng)
    {
        var deck = Enumerable.Range(0, Count).ToList();
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
        return deck;
    }

    /// <summary>Highest first, the way players hold their cards.</summary>
    public static List<int> SortForDisplay(IEnumerable<int> cards) =>
        cards.OrderByDescending(Rank).ThenBy(c => Suit(c)).ToList();

    /// <summary>How many cards of each rank (index 0–14).</summary>
    public static int[] RankCounts(IEnumerable<int> cards)
    {
        var counts = new int[RankCount];
        foreach (var c in cards) counts[Rank(c)]++;
        return counts;
    }
}
