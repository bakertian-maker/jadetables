using GameWeb.Games.Mahjong;
using GameWeb.Games.Rooms;
using Microsoft.Extensions.Localization;

namespace GameWeb.Localization;

/// <summary>Mahjong terms in the viewer's language: tiles, winds, bot names, faan patterns and log lines.</summary>
public sealed class GameText(IStringLocalizer<SharedResource> l)
{
    static readonly string[] Numerals = ["一", "二", "三", "四", "五", "六", "七", "八", "九"];
    static readonly string[] HansWinds = ["东", "南", "西", "北"];
    static readonly string[] HansBots = ["林", "陈", "黄", "何", "刘", "张"];
    static readonly string[] HantBots = ["林", "陳", "黃", "何", "劉", "張"];

    public string TileName(int k)
    {
        var script = Languages.Current;
        if (script == Script.Latin) return Tiles.Name(k);
        bool hant = script == Script.Traditional;
        return Tiles.SuitOf(k) switch
        {
            Suit.Characters => Numerals[k % 9] + (hant ? "萬" : "万"),
            Suit.Dots => Numerals[k % 9] + "筒",
            Suit.Bamboo => Numerals[k % 9] + (hant ? "索" : "条"),
            Suit.Wind => (hant ? Tiles.WindChinese[k - Tiles.East] : HansWinds[k - Tiles.East]) + (hant ? "風" : "风"),
            _ => (hant ? new[] { "紅中", "發財", "白板" } : new[] { "红中", "发财", "白板" })[k - Tiles.Red],
        };
    }

    /// <summary>The wind as one character, as shown on name plates and the compass.</summary>
    public string WindGlyph(int wind) => Languages.Current == Script.Simplified ? HansWinds[wind] : Tiles.WindChinese[wind];

    public string WindName(int wind) => l[Tiles.WindNames[wind]];

    public string BotName(int index) => index < 0 ? l["Bot"] : Languages.Current switch
    {
        Script.Simplified => "电脑·" + HansBots[index],
        Script.Traditional => "電腦·" + HantBots[index],
        _ => "Bot " + Room.BotNames[index],
    };

    public string SeatName(MjSeatView s) => s.IsBot ? BotName(s.BotIndex) : s.Name;

    public string SeatName(Seat s) => s.IsBot ? BotName(s.BotIndex) : s.Name;

    public string FaanName(FaanItem f) => Languages.Current switch
    {
        Script.Simplified => f.Simplified,
        Script.Traditional => f.Chinese,
        _ => f.Name,
    };

    /// <summary>The Chinese name to show beside the English one (traditional, as used in Hong Kong).</summary>
    public string FaanChinese(FaanItem f) => f.Chinese;

    public string LogLine(MjLogEntry e, Func<int, string> name) => e.Kind switch
    {
        MjLogKind.HandStart => l["Hand {0}: {1} round, {2} deals", e.Value, WindName(e.Other), name(e.Seat)],
        MjLogKind.ConcealedKong => l["{0} declares a concealed kong", name(e.Seat)],
        MjLogKind.AddKong => l["{0} adds to a kong of {1}", name(e.Seat), TileName(e.Tile)],
        MjLogKind.Pung => l["{0} pungs {1}", name(e.Seat), TileName(e.Tile)],
        MjLogKind.Kong => l["{0} kongs {1}", name(e.Seat), TileName(e.Tile)],
        MjLogKind.Chow => l["{0} chows {1}", name(e.Seat), TileName(e.Tile)],
        MjLogKind.SelfDrawWin => l["{0} wins by self-draw with {1} faan", name(e.Seat), e.Value],
        MjLogKind.DiscardWin => l["{0} wins on {1}'s {2} with {3} faan", name(e.Seat), name(e.Other), TileName(e.Tile), e.Value],
        MjLogKind.WallEmpty => l["The wall is empty: the hand is a draw"],
        _ => l["Match over"],
    };
}
