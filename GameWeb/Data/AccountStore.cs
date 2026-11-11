using System.Collections.Concurrent;
using GameWeb.Games.Archive;
using Microsoft.EntityFrameworkCore;

namespace GameWeb.Data;

/// <summary>How a finished game went for the player looking at their history.</summary>
public enum GameOutcome { Win, Loss, Draw, Played }

/// <summary>Another seat at the table: the computer (with its level) or another person.</summary>
public readonly record struct HistoryOpponent(bool IsBot, byte? BotLevel);

/// <summary>One line of a player's game history.</summary>
public sealed record HistoryEntry(
    long Id, GameType Type, DateTime EndedAt, GameOutcome Outcome,
    /// <summary>Xiangqi: 0 Red, 1 Black.</summary>
    byte? Side,
    /// <summary>Mahjong: final score and place (1 = most points; ties share a place).</summary>
    int? Score, int? Place,
    IReadOnlyList<HistoryOpponent> Opponents,
    /// <summary>Xiangqi: half-moves. Mahjong: hands.</summary>
    int Length,
    /// <summary>Xiangqi: how it ended (XqEndReason).</summary>
    byte Reason);

/// <summary>One line of the leaderboard.</summary>
public sealed record LeaderboardRow(int Rank, string DisplayName, int Rating, int Games, int Wins, int Draws, int Losses, bool IsMe);

public sealed record HistorySummary(int Games, int XiangqiWins, int XiangqiDraws, int XiangqiLosses, int MahjongMatches, int MahjongFirsts,
    int DouDizhuMatches = 0, int DouDizhuFirsts = 0);

/// <summary>Links browsers to accounts and reads a player's game history.</summary>
public sealed class AccountStore(IDbContextFactory<GamesDbContext> factory, ILogger<AccountStore> logger)
{
    // Pairs already linked in this run, so opening a room doesn't write to the database every time.
    readonly ConcurrentDictionary<(string UserId, string Key), bool> _linked = new();

    /// <summary>
    /// Records that this browser (its player id) belongs to the account. A browser already linked to a
    /// different account stays with that account. Never throws: history is not worth failing a page for.
    /// </summary>
    public async Task LinkAsync(string userId, string? playerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(playerId) || playerId.Length > 100) return;
        var key = SqlGameStore.PlayerKey(playerId);
        var hex = Convert.ToHexString(key);
        if (_linked.ContainsKey((userId, hex))) return;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.PlayerLinks.AnyAsync(l => l.PlayerKey == key, ct))
            {
                db.PlayerLinks.Add(new PlayerLink { PlayerKey = key, UserId = userId, LinkedAt = DateTime.UtcNow });
                await db.SaveChangesAsync(ct);
            }
            _linked[(userId, hex)] = true;
        }
        catch (DbUpdateException)
        {
            // Linked at the same moment by another request: nothing to do.
            _linked[(userId, hex)] = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not link a browser to account {UserId}", userId);
        }
    }

    /// <summary>The account's game settings: chime on or off, letter pieces in Xiangqi.</summary>
    public async Task<(bool SoundOn, bool LetterPieces)?> PrefsAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Users.Where(u => u.Id == userId)
                .Select(u => new ValueTuple<bool, bool>(u.SoundOn, u.LetterPieces))
                .Cast<(bool, bool)?>().FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read settings for account {UserId}", userId);
            return null;
        }
    }

    /// <summary>Saves a game setting on the account, so it follows the player to other devices.</summary>
    public async Task SavePrefsAsync(string userId, bool? soundOn = null, bool? letterPieces = null)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user is null) return;
            if (soundOn is bool sound) user.SoundOn = sound;
            if (letterPieces is bool letters) user.LetterPieces = letters;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save settings for account {UserId}", userId);
        }
    }

    /// <summary>Remembers the language the player last chose.</summary>
    public async Task SaveCultureAsync(string userId, string culture)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Culture, culture));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save the language for account {UserId}", userId);
        }
    }

    /// <summary>
    /// A finished game the account played, with the seat it played in; null if the game doesn't exist or
    /// the account wasn't in it (players can only replay their own games).
    /// </summary>
    public async Task<(GameRecord Game, byte MySeat)?> GameForUserAsync(string userId, long gameId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var game = await db.Games.AsNoTracking().Include(g => g.Players).FirstOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null) return null;
        var keys = (await db.PlayerLinks.Where(l => l.UserId == userId).Select(l => l.PlayerKey).ToListAsync(ct))
            .Select(Convert.ToHexString).ToHashSet();
        var me = game.Players.OrderBy(p => p.Seat).FirstOrDefault(p => p.PlayerKey is not null && keys.Contains(Convert.ToHexString(p.PlayerKey)));
        return me is null ? null : (game, me.Seat);
    }

    /// <summary>The account's rating in a game, or null before its first rated game.</summary>
    public async Task<PlayerRating?> RatingAsync(string userId, GameType type, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PlayerRatings.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.GameType == type, ct);
    }

    /// <summary>The best-rated players who have played enough rated games and chose to be listed.</summary>
    public async Task<IReadOnlyList<LeaderboardRow>> LeaderboardAsync(GameType type, int take, string? myUserId, CancellationToken ct = default)
    {
        // The board changes only when rated games finish, so it is read from the database at most once a
        // minute; "is this me" is worked out per visitor from the cached rows.
        var key = (type, take);
        if (!_leaderboard.TryGetValue(key, out var cached) || DateTime.UtcNow - cached.At > LeaderboardCacheTime)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await (from r in db.PlayerRatings.AsNoTracking()
                              join u in db.Users on r.UserId equals u.Id
                              where r.GameType == type && r.Games >= Ratings.LeaderboardMinGames && u.ShowOnLeaderboard
                              orderby r.Rating descending, r.Games descending
                              select new LeaderboardEntry(u.Id, u.DisplayName, r.Rating, r.Games, r.Wins, r.Draws, r.Losses))
                             .Take(take).ToListAsync(ct);
            cached = (DateTime.UtcNow, rows);
            _leaderboard[key] = cached;
        }
        return cached.Rows.Select((x, i) => new LeaderboardRow(i + 1, x.DisplayName, x.Rating, x.Games, x.Wins, x.Draws, x.Losses, x.UserId == myUserId)).ToList();
    }

    /// <summary>Forgets the cached leaderboard, e.g. after a player hides themselves or deletes their account.</summary>
    public void RefreshLeaderboard() => _leaderboard.Clear();

    static readonly TimeSpan LeaderboardCacheTime = TimeSpan.FromMinutes(1);
    readonly ConcurrentDictionary<(GameType, int), (DateTime At, List<LeaderboardEntry> Rows)> _leaderboard = new();
    sealed record LeaderboardEntry(string UserId, string DisplayName, int Rating, int Games, int Wins, int Draws, int Losses);

    /// <summary>The account's place on the leaderboard, or null if it isn't listed.</summary>
    public async Task<int?> RankAsync(string userId, GameType type, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var me = await (from r in db.PlayerRatings
                        join u in db.Users on r.UserId equals u.Id
                        where r.UserId == userId && r.GameType == type
                        select new { r.Rating, r.Games, u.ShowOnLeaderboard }).FirstOrDefaultAsync(ct);
        if (me is null || me.Games < Ratings.LeaderboardMinGames || !me.ShowOnLeaderboard) return null;
        return 1 + await (from r in db.PlayerRatings
                          join u in db.Users on r.UserId equals u.Id
                          where r.GameType == type && r.Games >= Ratings.LeaderboardMinGames && u.ShowOnLeaderboard && r.Rating > me.Rating
                          select r).CountAsync(ct);
    }

    /// <summary>Forgets the cached links of an account (after it is deleted).</summary>
    public void Forget(string userId)
    {
        foreach (var k in _linked.Keys.Where(k => k.UserId == userId)) _linked.TryRemove(k, out _);
    }

    /// <summary>The account's finished games, newest first.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(string userId, int skip, int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var keys = await db.PlayerLinks.Where(l => l.UserId == userId).Select(l => l.PlayerKey).ToListAsync(ct);
        if (keys.Count == 0) return [];
        var mine = keys.Select(Convert.ToHexString).ToHashSet();
        var keyQuery = db.PlayerLinks.Where(l => l.UserId == userId).Select(l => l.PlayerKey);
        var games = await db.Games.AsNoTracking()
            .Where(g => g.Players.Any(p => keyQuery.Contains(p.PlayerKey!)))
            .OrderByDescending(g => g.EndedAt).ThenByDescending(g => g.Id)
            .Skip(skip).Take(take)
            .Include(g => g.Players)
            .AsSplitQuery()
            .ToListAsync(ct);
        return games.Select(g => Describe(g, mine)).OfType<HistoryEntry>().ToList();
    }

    /// <summary>Totals for the top of the history page.</summary>
    public async Task<HistorySummary> SummaryAsync(string userId, CancellationToken ct = default)
    {
        var all = await HistoryAsync(userId, 0, int.MaxValue, ct);
        return new HistorySummary(
            all.Count,
            all.Count(e => e.Type == GameType.Xiangqi && e.Outcome == GameOutcome.Win),
            all.Count(e => e.Type == GameType.Xiangqi && e.Outcome == GameOutcome.Draw),
            all.Count(e => e.Type == GameType.Xiangqi && e.Outcome == GameOutcome.Loss),
            all.Count(e => e.Type == GameType.Mahjong),
            all.Count(e => e.Type == GameType.Mahjong && e.Place == 1),
            all.Count(e => e.Type == GameType.DouDizhu),
            all.Count(e => e.Type == GameType.DouDizhu && e.Place == 1));
    }

    /// <summary>Works out a game from the point of view of the seat whose player key is one of <paramref name="myKeys"/>.</summary>
    public static HistoryEntry? Describe(GameRecord g, ISet<string> myKeys)
    {
        var me = g.Players.OrderBy(p => p.Seat).FirstOrDefault(p => p.PlayerKey is not null && myKeys.Contains(Convert.ToHexString(p.PlayerKey)));
        if (me is null) return null;
        var opponents = g.Players.Where(p => p.Seat != me.Seat).OrderBy(p => p.Seat)
            .Select(p => new HistoryOpponent(p.IsBot, p.BotLevel)).ToList();

        if (g.GameType == GameType.Xiangqi)
        {
            // Result: 1 Red wins, 2 Black wins, 3 draw. Side: 0 Red, 1 Black.
            var outcome = g.Result switch
            {
                3 => GameOutcome.Draw,
                1 => me.Side == 0 ? GameOutcome.Win : GameOutcome.Loss,
                2 => me.Side == 1 ? GameOutcome.Win : GameOutcome.Loss,
                _ => GameOutcome.Played,
            };
            return new HistoryEntry(g.Id, g.GameType, g.EndedAt, outcome, me.Side, null, null, opponents, g.PlyCount, g.Reason);
        }

        int? place = me.Score is int score ? 1 + g.Players.Count(p => p.Score > score) : null;
        var mjOutcome = place == 1 ? GameOutcome.Win : GameOutcome.Played;
        return new HistoryEntry(g.Id, g.GameType, g.EndedAt, mjOutcome, null, me.Score, place, opponents, g.PlyCount, g.Reason);
    }
}
