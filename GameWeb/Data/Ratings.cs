using GameWeb.Games.Archive;
using GameWeb.Games.Xiangqi;
using Microsoft.EntityFrameworkCore;

namespace GameWeb.Data;

/// <summary>
/// Xiangqi ratings (Elo). After each saved game, every seat linked to an account is rated against its
/// opponent: the computer counts at a fixed rating for its level, another account at its current rating.
/// Games against a guest aren't rated (a guest has no rating to play against).
/// </summary>
public static class Ratings
{
    public const int Start = 1200;
    /// <summary>Games before a player appears on the leaderboard.</summary>
    public const int LeaderboardMinGames = 5;
    /// <summary>Faster movement while a rating is still finding its level.</summary>
    const int ProvisionalGames = 20;

    /// <summary>The computer's fixed rating at each level.</summary>
    public static int BotRating(XqLevel level) => level switch
    {
        XqLevel.Beginner => 800,
        XqLevel.Medium => 1200,
        _ => 1600,
    };

    /// <summary>The new rating after one game. <paramref name="score"/>: 1 win, 0.5 draw, 0 loss.</summary>
    public static int Update(int rating, int opponent, double score, int gamesPlayed)
    {
        double expected = 1 / (1 + Math.Pow(10, (opponent - rating) / 400.0));
        int k = gamesPlayed < ProvisionalGames ? 40 : 24;
        return (int)Math.Round(rating + k * (score - expected));
    }

    /// <summary>Rates a just-saved Xiangqi game for the accounts that played it. Call with the game's players.</summary>
    public static async Task ApplyAsync(GamesDbContext db, GameRecord game)
    {
        if (game.GameType != GameType.Xiangqi || game.Result is < 1 or > 3 || game.Players.Count != 2) return;

        var keys = game.Players.Where(p => p.PlayerKey is not null).Select(p => p.PlayerKey!).ToList();
        if (keys.Count == 0) return;
        var links = await db.PlayerLinks.Where(l => keys.Contains(l.PlayerKey)).ToListAsync();
        string? UserOf(GamePlayerRecord p) =>
            p.PlayerKey is null ? null : links.FirstOrDefault(l => l.PlayerKey.SequenceEqual(p.PlayerKey))?.UserId;

        var seats = game.Players.OrderBy(p => p.Seat).ToArray();
        var users = seats.Select(UserOf).ToArray();
        if (users[0] is not null && users[0] == users[1]) return;   // one account on both sides: not a real game

        var ratings = new PlayerRating?[2];
        for (int i = 0; i < 2; i++)
        {
            if (users[i] is not { } userId) continue;
            ratings[i] = await db.PlayerRatings.FindAsync(userId, GameType.Xiangqi);
            if (ratings[i] is null)
            {
                ratings[i] = new PlayerRating { UserId = userId, GameType = GameType.Xiangqi };
                db.PlayerRatings.Add(ratings[i]!);
            }
        }

        // Opponents' ratings as they were before this game.
        var before = ratings.Select(r => r?.Rating).ToArray();
        for (int i = 0; i < 2; i++)
        {
            if (ratings[i] is not { } mine) continue;
            var other = seats[1 - i];
            int? opponent = other.IsBot ? BotRating((XqLevel)(other.BotLevel ?? (byte)XqLevel.Medium)) : before[1 - i];
            if (opponent is null) continue;   // a guest

            // Result: 1 Red wins, 2 Black wins, 3 draw. Side: 0 Red, 1 Black.
            double score = game.Result == 3 ? 0.5 : (game.Result == 1) == (seats[i].Side == 0) ? 1 : 0;
            mine.Rating = Update(mine.Rating, opponent.Value, score, mine.Games);
            mine.Games++;
            if (score == 1) mine.Wins++;
            else if (score == 0) mine.Losses++;
            else mine.Draws++;
            mine.UpdatedAt = DateTime.UtcNow;
        }
        // A new player who only met a guest has nothing to record.
        foreach (var r in ratings)
            if (r is { Games: 0 } && db.Entry(r).State == EntityState.Added) db.Entry(r).State = EntityState.Detached;
        await db.SaveChangesAsync();
    }
}
