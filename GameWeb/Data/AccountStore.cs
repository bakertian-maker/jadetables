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

public sealed record HistorySummary(int Games, int XiangqiWins, int XiangqiDraws, int XiangqiLosses, int MahjongMatches, int MahjongFirsts);

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
            all.Count(e => e.Type == GameType.Mahjong && e.Place == 1));
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
