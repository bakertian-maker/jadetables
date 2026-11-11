using GameWeb.Games.DouDizhu;
using Microsoft.Extensions.Localization;

namespace GameWeb.Localization;

/// <summary>Dou Dizhu words in the viewer's language: card and play names, roles.</summary>
public sealed class DdzText(IStringLocalizer<SharedResource> l)
{
    static readonly string[] SuitNames = ["Spades", "Hearts", "Clubs", "Diamonds"];

    /// <summary>For screen readers: "10 of Hearts", "Big joker".</summary>
    public string CardName(int card) => card switch
    {
        Cards.BigJoker => l["Big joker"],
        Cards.SmallJoker => l["Small joker"],
        _ => l["{0} of {1}", Cards.RankLabel(card), l[SuitNames[Cards.Suit(card)]]],
    };

    /// <summary>The word on a joker card: 大王 / 小王, or JOKER.</summary>
    public string JokerWord(int card) => Languages.IsChinese
        ? (card == Cards.BigJoker ? "大王" : "小王")
        : "JOKER";

    public string LevelName(DdzLevel level) => level switch
    {
        DdzLevel.Beginner => l["Beginner"],
        DdzLevel.Medium => l["Medium"],
        _ => l["Difficult"],
    };

    public string KindName(DdzKind kind) => kind switch
    {
        DdzKind.Single => l["Single"],
        DdzKind.Pair => l["Pair"],
        DdzKind.Triple => l["Three of a kind"],
        DdzKind.TripleSingle => l["Three with one"],
        DdzKind.TriplePair => l["Three with a pair"],
        DdzKind.Straight => l["Straight"],
        DdzKind.PairStraight => l["Pairs in a row"],
        DdzKind.Airplane => l["Airplane"],
        DdzKind.AirplaneSingles => l["Airplane with singles"],
        DdzKind.AirplanePairs => l["Airplane with pairs"],
        DdzKind.FourTwoSingles => l["Four with two"],
        DdzKind.FourTwoPairs => l["Four with two pairs"],
        DdzKind.Bomb => l["Bomb"],
        DdzKind.Rocket => l["Rocket"],
        _ => "",
    };

    public string Role(bool landlord) => landlord ? l["Landlord"] : l["Farmer"];

    public string BidText(int? bid) => bid switch
    {
        null => "",
        0 => l["No bid"],
        _ => l["{0} pt", bid.Value],
    };
}
