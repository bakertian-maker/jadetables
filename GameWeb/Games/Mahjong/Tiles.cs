namespace GameWeb.Games.Mahjong;

public enum Suit { Characters, Dots, Bamboo, Wind, Dragon }

/// <summary>
/// Tiles are identified by kind, 0..33:
/// 0-8 Characters (萬) 1-9, 9-17 Dots (筒) 1-9, 18-26 Bamboo (索) 1-9,
/// 27-30 East, South, West, North winds, 31-33 Red (中), Green (發), White (白) dragons.
/// A full set has four copies of each kind (136 tiles, no flowers).
/// </summary>
public static class Tiles
{
    public const int KindCount = 34;
    public const int East = 27, South = 28, West = 29, North = 30;
    public const int Red = 31, Green = 32, White = 33;

    public static readonly string[] WindNames = ["East", "South", "West", "North"];
    public static readonly string[] WindChinese = ["東", "南", "西", "北"];
    static readonly string[] Numerals = ["一", "二", "三", "四", "五", "六", "七", "八", "九"];
    static readonly string[] DragonNames = ["Red", "Green", "White"];

    public static readonly int[] Orphans = [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33];

    public static Suit SuitOf(int k) => k switch
    {
        < 9 => Suit.Characters,
        < 18 => Suit.Dots,
        < 27 => Suit.Bamboo,
        < 31 => Suit.Wind,
        _ => Suit.Dragon,
    };

    public static bool IsSuited(int k) => k < 27;
    public static bool IsHonor(int k) => k >= 27;
    public static bool IsWind(int k) => k is >= 27 and <= 30;
    public static bool IsDragon(int k) => k >= 31;
    public static int Rank(int k) => k < 27 ? k % 9 + 1 : 0;
    public static bool IsTerminal(int k) => k < 27 && (k % 9 == 0 || k % 9 == 8);
    public static bool IsTerminalOrHonor(int k) => IsHonor(k) || IsTerminal(k);
    public static int WindTile(int wind) => East + wind;
    public static bool SameSuit(int a, int b) => IsSuited(a) && IsSuited(b) && a / 9 == b / 9;

    public static string Name(int k) => SuitOf(k) switch
    {
        Suit.Characters => $"{Rank(k)} Characters",
        Suit.Dots => $"{Rank(k)} Dots",
        Suit.Bamboo => $"{Rank(k)} Bamboo",
        Suit.Wind => WindNames[k - East] + " Wind",
        _ => DragonNames[k - Red] + " Dragon",
    };

    public static string ChineseName(int k) => SuitOf(k) switch
    {
        Suit.Characters => Numerals[k % 9] + "萬",
        Suit.Dots => Numerals[k % 9] + "筒",
        Suit.Bamboo => Numerals[k % 9] + "索",
        Suit.Wind => WindChinese[k - East],
        _ => "中發白"[k - Red].ToString(),
    };

    public static string Numeral(int rank) => Numerals[rank - 1];

    public static int[] ToCounts(IEnumerable<int> tiles)
    {
        var c = new int[KindCount];
        foreach (var t in tiles) c[t]++;
        return c;
    }

    /// <summary>
    /// Parses compact notation used by tests: digits followed by a suit letter.
    /// m = characters, p = dots, s = bamboo, z = honours (1-4 E S W N, 5 red, 6 green, 7 white).
    /// Example: "123m456p789s1155z".
    /// </summary>
    public static List<int> Parse(string text)
    {
        var result = new List<int>();
        var pending = new List<int>();
        foreach (var ch in text)
        {
            if (char.IsDigit(ch)) { pending.Add(ch - '0'); continue; }
            int offset = ch switch
            {
                'm' => 0,
                'p' => 9,
                's' => 18,
                'z' => 27,
                _ => throw new FormatException($"Unknown suit '{ch}'"),
            };
            foreach (var n in pending) result.Add(offset + n - 1);
            pending.Clear();
        }
        if (pending.Count > 0) throw new FormatException("Digits without suit");
        return result;
    }
}
