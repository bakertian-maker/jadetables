namespace GameWeb.Games.Mahjong;

/// <summary>A scoring pattern. Chinese is the traditional (Hong Kong) name, Simplified the mainland form.</summary>
public sealed record FaanItem(string Name, string Chinese, int Faan, bool Limit = false)
{
    public string Simplified => SimplifiedNames.TryGetValue(Chinese, out var s) ? s : Chinese;

    static readonly Dictionary<string, string> SimplifiedNames = new()
    {
        ["門前清"] = "门前清", ["門風"] = "门风", ["圈風"] = "圈风", ["海底撈月"] = "海底捞月",
        ["河底撈魚"] = "河底捞鱼", ["槓上開花"] = "杠上开花", ["搶槓"] = "抢杠", ["混么九"] = "混幺九",
        ["對對糊"] = "对对和", ["平糊"] = "平和", ["三元牌"] = "箭刻", ["混一色"] = "混一色",
        ["小四喜"] = "小四喜", ["大四喜"] = "大四喜", ["十三么"] = "十三幺", ["清么九"] = "清幺九",
        ["字一色"] = "字一色", ["十八羅漢"] = "十八罗汉", ["天糊"] = "天和", ["地糊"] = "地和",
        ["坎坎糊"] = "坎坎和", ["自摸"] = "自摸",
    };
}

public sealed record ScoreResult(IReadOnlyList<FaanItem> Items, int Total, bool IsLimit);

public sealed class WinContext
{
    /// <summary>Concealed tile counts including the winning tile.</summary>
    public required int[] Concealed { get; init; }
    public required IReadOnlyList<Meld> Melds { get; init; }
    public required int WinTile { get; init; }
    public bool SelfDrawn { get; init; }
    public int SeatWind { get; init; }
    public int PrevailingWind { get; init; }
    public bool LastTile { get; init; }
    public bool KongReplacement { get; init; }
    public bool RobbingKong { get; init; }
    public bool Heavenly { get; init; }
    public bool Earthly { get; init; }
    public int LimitFaan { get; init; } = 10;
}

/// <summary>Hong Kong (Cantonese) faan scoring, no flowers.</summary>
public static class Scoring
{
    public static class F
    {
        public static readonly FaanItem SelfDrawn = new("Self-drawn", "自摸", 1);
        public static readonly FaanItem Concealed = new("Concealed hand", "門前清", 1);
        public static readonly FaanItem AllChows = new("All chows", "平糊", 1);
        public static readonly FaanItem DragonPung = new("Dragon pung", "三元牌", 1);
        public static readonly FaanItem SeatWind = new("Seat wind pung", "門風", 1);
        public static readonly FaanItem PrevailingWind = new("Prevailing wind pung", "圈風", 1);
        public static readonly FaanItem LastTileDraw = new("Win on the last tile", "海底撈月", 1);
        public static readonly FaanItem LastTileDiscard = new("Win on the last discard", "河底撈魚", 1);
        public static readonly FaanItem KongReplacement = new("Win on a kong replacement", "槓上開花", 1);
        public static readonly FaanItem RobbingKong = new("Robbing a kong", "搶槓", 1);
        public static readonly FaanItem MixedTerminals = new("Mixed terminals and honours", "混么九", 1);
        public static readonly FaanItem AllPungs = new("All pungs", "對對糊", 3);
        public static readonly FaanItem MixedOneSuit = new("Mixed one suit", "混一色", 3);
        public static readonly FaanItem SmallDragons = new("Small three dragons", "小三元", 5);
        public static readonly FaanItem SmallWinds = new("Small four winds", "小四喜", 6);
        public static readonly FaanItem PureOneSuit = new("Pure one suit", "清一色", 7);
        public static readonly FaanItem BigDragons = new("Great three dragons", "大三元", 8);
        public static readonly FaanItem ThirteenOrphans = new("Thirteen orphans", "十三么", 0, true);
        public static readonly FaanItem BigWinds = new("Great four winds", "大四喜", 0, true);
        public static readonly FaanItem AllHonours = new("All honours", "字一色", 0, true);
        public static readonly FaanItem AllTerminals = new("All terminals", "清么九", 0, true);
        public static readonly FaanItem FourConcealedPungs = new("Four concealed pungs", "坎坎糊", 0, true);
        public static readonly FaanItem FourKongs = new("Eighteen arhats (four kongs)", "十八羅漢", 0, true);
        public static readonly FaanItem Heavenly = new("Heavenly hand", "天糊", 0, true);
        public static readonly FaanItem Earthly = new("Earthly hand", "地糊", 0, true);
    }

    /// <summary>Every scoring pattern, in the order shown on the rules page.</summary>
    public static readonly FaanItem[] AllPatterns =
    [
        F.SelfDrawn, F.Concealed, F.AllChows, F.DragonPung, F.SeatWind, F.PrevailingWind,
        F.MixedTerminals, F.LastTileDraw, F.LastTileDiscard, F.KongReplacement, F.RobbingKong,
        F.AllPungs, F.MixedOneSuit, F.SmallDragons, F.SmallWinds, F.PureOneSuit, F.BigDragons,
        F.ThirteenOrphans, F.BigWinds, F.AllHonours, F.AllTerminals, F.FourConcealedPungs, F.FourKongs,
        F.Heavenly, F.Earthly,
    ];

    /// <summary>Points a faan total is worth (half-spicy style table).</summary>
    public static int BasePoints(int faan) => Math.Clamp(faan, 0, 13) switch
    {
        0 => 1, 1 => 2, 2 => 4, 3 => 8, 4 => 16, 5 => 24, 6 => 32,
        7 => 48, 8 => 64, 9 => 96, 10 => 128, 11 => 192, 12 => 256, _ => 384,
    };

    /// <summary>Scores a complete hand; returns null if the tiles do not form a winning hand.</summary>
    public static ScoreResult? Evaluate(WinContext ctx)
    {
        if (ctx.Melds.Count == 0 && HandAnalyzer.IsThirteenOrphans(ctx.Concealed))
            return Limit(ctx, F.ThirteenOrphans);

        ScoreResult? best = null;
        foreach (var d in HandAnalyzer.Decompose(ctx.Concealed))
        {
            var r = EvaluateDecomposition(ctx, d);
            if (best is null || r.Total > best.Total || (r.Total == best.Total && r.Items.Count > best.Items.Count))
                best = r;
        }
        return best;
    }

    static ScoreResult Limit(WinContext ctx, params FaanItem[] items) =>
        new(items, ctx.LimitFaan, true);

    static ScoreResult EvaluateDecomposition(WinContext ctx, Decomposition d)
    {
        var sets = new List<SetInfo>(d.Sets);
        foreach (var m in ctx.Melds) sets.Add(new SetInfo(m.Type, m.Tile, m.Concealed));

        var tiles = new List<int> { d.Pair, d.Pair };
        foreach (var s in sets)
        {
            if (s.Type == MeldType.Chow) tiles.AddRange([s.Tile, s.Tile + 1, s.Tile + 2]);
            else tiles.AddRange([s.Tile, s.Tile, s.Tile]);
        }

        var pungs = sets.Where(s => s.Type != MeldType.Chow).ToList();
        int kongs = ctx.Melds.Count(m => m.Type == MeldType.Kong);
        bool allPungs = pungs.Count == 4;
        bool concealedHand = ctx.Melds.All(m => m.Concealed);
        bool hasHonor = tiles.Any(Tiles.IsHonor);
        var suits = tiles.Where(Tiles.IsSuited).Select(t => t / 9).Distinct().Count();
        int dragonPungs = pungs.Count(s => Tiles.IsDragon(s.Tile));
        int windPungs = pungs.Count(s => Tiles.IsWind(s.Tile));

        // Limit hands.
        var limits = new List<FaanItem>();
        if (ctx.Heavenly) limits.Add(F.Heavenly);
        if (ctx.Earthly) limits.Add(F.Earthly);
        if (allPungs)
        {
            int concealedPungs = pungs.Count(s => s.Concealed);
            // A pung completed by a discard counts as exposed (unless the winning tile completed the pair).
            if (!ctx.SelfDrawn && d.Pair != ctx.WinTile && d.Sets.Any(s => s.Type != MeldType.Chow && s.Tile == ctx.WinTile))
                concealedPungs--;
            if (concealedPungs == 4) limits.Add(F.FourConcealedPungs);
        }
        if (kongs == 4) limits.Add(F.FourKongs);
        if (tiles.All(Tiles.IsHonor)) limits.Add(F.AllHonours);
        if (tiles.All(Tiles.IsTerminal)) limits.Add(F.AllTerminals);
        if (windPungs == 4) limits.Add(F.BigWinds);
        if (limits.Count > 0) return new ScoreResult(limits, ctx.LimitFaan, true);

        var items = new List<FaanItem>();
        if (ctx.SelfDrawn) items.Add(F.SelfDrawn);
        if (concealedHand) items.Add(F.Concealed);
        if (pungs.Count == 0) items.Add(F.AllChows);
        if (allPungs) items.Add(F.AllPungs);

        if (suits == 1 && !hasHonor) items.Add(F.PureOneSuit);
        else if (suits == 1 && hasHonor) items.Add(F.MixedOneSuit);

        if (dragonPungs == 3) items.Add(F.BigDragons);
        else if (dragonPungs == 2 && Tiles.IsDragon(d.Pair)) items.Add(F.SmallDragons);
        else for (int i = 0; i < dragonPungs; i++) items.Add(F.DragonPung);

        if (windPungs == 3 && Tiles.IsWind(d.Pair)) items.Add(F.SmallWinds);
        else
        {
            if (pungs.Any(s => s.Tile == Tiles.WindTile(ctx.SeatWind))) items.Add(F.SeatWind);
            if (pungs.Any(s => s.Tile == Tiles.WindTile(ctx.PrevailingWind))) items.Add(F.PrevailingWind);
        }

        if (tiles.All(Tiles.IsTerminalOrHonor) && hasHonor) items.Add(F.MixedTerminals);
        if (ctx.LastTile) items.Add(ctx.SelfDrawn ? F.LastTileDraw : F.LastTileDiscard);
        if (ctx.KongReplacement) items.Add(F.KongReplacement);
        if (ctx.RobbingKong) items.Add(F.RobbingKong);

        int sum = items.Sum(i => i.Faan);
        return new ScoreResult(items, Math.Min(sum, ctx.LimitFaan), sum >= ctx.LimitFaan);
    }
}
