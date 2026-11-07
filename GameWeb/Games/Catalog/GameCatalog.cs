namespace GameWeb.Games.Catalog;

public enum GameStatus { Playable, InDevelopment }

public sealed record GameInfo(
    string Key, string Name, string ChineseName, string SimplifiedName, string Tagline, string Description,
    string Url, string Players, GameStatus Status);

/// <summary>The games listed on the home page. Text fields are English keys translated in the UI.</summary>
public sealed class GameCatalog
{
    public IReadOnlyList<GameInfo> Games { get; } =
    [
        new("mahjong", "Hong Kong Mahjong", "廣東麻雀", "广东麻将", "Four players, 136 tiles, one winning hand.",
            "Classic Cantonese rules with faan scoring. Play the computer straight away, or open a private room and send the link to three friends.",
            "/mahjong", "4 players", GameStatus.Playable),
        new("xiangqi", "Xiangqi", "象棋", "象棋", "Chinese chess on the river board.",
            "Two generals, one river. Play a friend or a computer opponent from beginner to master.",
            "", "2 players", GameStatus.InDevelopment),
        new("doudizhu", "Dou Dizhu", "鬥地主", "斗地主", "Fight the landlord.",
            "Three players, one landlord. Bid, team up and empty your hand first.",
            "", "3 players", GameStatus.InDevelopment),
    ];

    public GameInfo? Find(string key) => Games.FirstOrDefault(g => g.Key == key);
}
