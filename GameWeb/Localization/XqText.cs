using GameWeb.Games.Xiangqi;
using Microsoft.Extensions.Localization;

namespace GameWeb.Localization;

/// <summary>Xiangqi terms in the viewer's language: piece characters and names, move notation, end reasons.</summary>
public sealed class XqText(IStringLocalizer<SharedResource> l)
{
    // Index by piece type 1..7: general, advisor, elephant, horse, chariot, cannon, soldier.
    static readonly string[] HantRed = ["", "帥", "仕", "相", "傌", "俥", "炮", "兵"];
    static readonly string[] HantBlack = ["", "將", "士", "象", "馬", "車", "砲", "卒"];
    static readonly string[] HansRed = ["", "帅", "仕", "相", "马", "车", "炮", "兵"];
    static readonly string[] HansBlack = ["", "将", "士", "象", "马", "车", "炮", "卒"];
    static readonly string[] Letters = ["", "K", "A", "E", "H", "R", "C", "P"];
    static readonly string[] EnglishNames = ["", "General", "Advisor", "Elephant", "Horse", "Chariot", "Cannon", "Soldier"];
    static readonly string[] ChineseNumerals = ["", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
    static readonly string[] WideDigits = ["", "１", "２", "３", "４", "５", "６", "７", "８", "９"];

    /// <summary>The character on the piece. Traditional unless the viewer reads Simplified Chinese.</summary>
    public string PieceChar(int piece)
    {
        int type = Pieces.Type(piece);
        bool red = piece > 0;
        return Languages.Current == Script.Simplified
            ? (red ? HansRed : HansBlack)[type]
            : (red ? HantRed : HantBlack)[type];
    }

    public static string PieceLetter(int piece) => Letters[Pieces.Type(piece)];

    public string PieceName(int piece) => l[EnglishNames[Pieces.Type(piece)]];

    public string SideName(XqSide side) => side == XqSide.Red ? l["Red"] : l["Black"];

    public string LevelName(XqLevel level) => level switch
    {
        XqLevel.Beginner => l["Beginner"],
        XqLevel.Medium => l["Medium"],
        _ => l["Difficult"],
    };

    public static string SquareName(int sq) => Sq.Name(sq);

    /// <summary>"炮二平五" / "马8进7" in Chinese, "C2.5" / "H8+7" (WXF style) in English.</summary>
    public string Notation(XqNotation n)
    {
        var script = Languages.Current;
        if (script == Script.Latin)
        {
            string prefix = n.Tandem == 0 ? "" : n.TandemCount switch
            {
                2 => n.Tandem == 1 ? "+" : "-",
                3 => new[] { "+", ".", "-" }[n.Tandem - 1],
                _ => n.Tandem.ToString(),
            };
            string from = n.Tandem == 0 ? n.FromFile.ToString() : "";
            char op = n.Op == '=' ? '.' : n.Op;
            return $"{prefix}{PieceLetter(n.Piece)}{from}{op}{n.Dest}";
        }

        bool hans = script == Script.Simplified;
        bool red = n.Side == XqSide.Red;
        string Num(int v) => red ? ChineseNumerals[v] : WideDigits[v];
        string piece = PieceChar(n.Piece);
        string chineseOp = n.Op switch
        {
            '+' => hans ? "进" : "進",
            '-' => "退",
            _ => "平",
        };
        if (n.Tandem == 0) return $"{piece}{Num(n.FromFile)}{chineseOp}{Num(n.Dest)}";
        string rear = hans ? "后" : "後";
        string tandem = n.TandemCount switch
        {
            2 => n.Tandem == 1 ? "前" : rear,
            3 => new[] { "前", "中", rear }[n.Tandem - 1],
            _ => ChineseNumerals[n.Tandem],
        };
        return $"{tandem}{piece}{chineseOp}{Num(n.Dest)}";
    }

    /// <summary>The room's time limit in a few words, e.g. "10 min each, +5s per move".</summary>
    public string TimeControl(XqRoomSettings s) =>
        s.HasClock ? l["{0} min each, +{1}s per move", s.ClockMinutes, s.IncrementSeconds]
        : s.MoveSeconds > 0 ? l["{0}s per move", s.MoveSeconds]
        : l["No move timer"];

    /// <summary>How a game ended, in a word or two, for the game history.</summary>
    public string ShortReason(XqEndReason reason) => reason switch
    {
        XqEndReason.Checkmate => l["checkmate"],
        XqEndReason.Stalemate => l["no legal moves"],
        XqEndReason.Resignation => l["resignation"],
        XqEndReason.Timeout => l["time"],
        XqEndReason.NoCaptureLimit => l["60-move rule"],
        XqEndReason.NoAttackers => l["no attackers left"],
        XqEndReason.Agreement => l["agreed"],
        XqEndReason.Repetition => l["repetition"],
        XqEndReason.PerpetualCheck => l["perpetual check"],
        XqEndReason.PerpetualChase => l["perpetual chase"],
        _ => "",
    };

    public string ReasonText(XqEndReason reason, string loserName) => reason switch
    {
        XqEndReason.Checkmate => l["Checkmate."],
        XqEndReason.Stalemate => l["No legal moves left. In Xiangqi that counts as a loss."],
        XqEndReason.Resignation => l["{0} resigned.", loserName],
        XqEndReason.Timeout => l["{0} ran out of time.", loserName],
        XqEndReason.NoCaptureLimit => l["60 moves without a capture: the game is drawn."],
        XqEndReason.NoAttackers => l["Neither side has pieces left that can attack: the game is drawn."],
        XqEndReason.Agreement => l["Draw agreed."],
        XqEndReason.Repetition => l["The same position came up three times: the game is drawn."],
        XqEndReason.PerpetualCheck => l["{0} gave perpetual check, which loses.", loserName],
        XqEndReason.PerpetualChase => l["{0} kept chasing a piece (perpetual chase), which loses.", loserName],
        _ => "",
    };
}
