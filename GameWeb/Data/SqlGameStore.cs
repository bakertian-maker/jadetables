using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using GameWeb.Games.Archive;
using GameWeb.Games.Xiangqi;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GameWeb.Data;

/// <summary>
/// SQL Server game store. Finished games are queued and written by one background loop, so a slow
/// or unavailable database never blocks a game; failures are logged and the game is dropped.
/// Opening-book lookups go straight to the database (a clustered-index seek).
/// </summary>
public sealed class SqlGameStore(IDbContextFactory<GamesDbContext> dbFactory, ILogger<SqlGameStore> logger)
    : BackgroundService, IGameArchive, IOpeningBook
{
    const int QueueCapacity = 1000;

    readonly Channel<FinishedGame> _queue = Channel.CreateBounded<FinishedGame>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Save(FinishedGame game)
    {
        if (!_queue.Writer.TryWrite(game))
            logger.LogWarning("Game archive queue is full or closed; {Type} game in room {Code} was not saved", game.Type, game.RoomCode);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read until the queue is completed at shutdown, so queued games are still written.
        await foreach (var game in _queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                await WriteAsync(game);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not save {Type} game from room {Code}", game.Type, game.RoomCode);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    async Task WriteAsync(FinishedGame game)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var record = new GameRecord
        {
            GameType = game.Type,
            RoomCode = game.RoomCode,
            StartedAt = game.StartedAt,
            EndedAt = game.EndedAt,
            Result = game.Result,
            Reason = game.Reason,
            PlyCount = (short)Math.Min(game.PlyCount, short.MaxValue),
            Moves = game.Moves,
            Details = game.Details,
            Players = game.Players.Select(p => new GamePlayerRecord
            {
                Seat = (byte)p.Seat,
                Side = p.Side,
                PlayerKey = p.PlayerId is null ? null : PlayerKey(p.PlayerId),
                IsBot = p.IsBot,
                BotLevel = p.BotLevel,
                Score = p.Score,
            }).ToList(),
        };
        db.Games.Add(record);
        await db.SaveChangesAsync();

        // Ratings for the accounts that played (a failure here doesn't lose the saved game).
        try
        {
            await Ratings.ApplyAsync(db, record);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not update ratings for the game from room {Code}", game.RoomCode);
        }

        if (game.BookMoves.Count > 0) await RecordBookAsync(db, game.BookMoves, game.Result);
        logger.LogInformation("Saved {Type} game from room {Code} ({Plies} plies, {Book} book moves)",
            game.Type, game.RoomCode, game.PlyCount, game.BookMoves.Count);
    }

    /// <summary>One call to dbo.Book_RecordGame with all of the game's opening moves as a table-valued parameter.</summary>
    internal static async Task RecordBookAsync(GamesDbContext db, IReadOnlyList<(ulong Hash, int Move)> moves, byte result)
    {
        var table = new DataTable();
        table.Columns.Add("PositionHash", typeof(long));
        table.Columns.Add("Move", typeof(short));
        foreach (var (hash, move) in moves.Distinct())
            table.Rows.Add(unchecked((long)hash), (short)move);

        var movesParam = new SqlParameter("@Moves", SqlDbType.Structured) { TypeName = "dbo.BookMoveList", Value = table };
        var resultParam = new SqlParameter("@Result", SqlDbType.TinyInt) { Value = result };
        await db.Database.ExecuteSqlRawAsync("EXEC dbo.Book_RecordGame @Moves, @Result", movesParam, resultParam);
    }

    public async Task<IReadOnlyList<BookMove>> LookupAsync(ulong positionHash, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await QueryBookAsync(db, positionHash, cancellationToken);
    }

    internal static async Task<IReadOnlyList<BookMove>> QueryBookAsync(GamesDbContext db, ulong positionHash, CancellationToken cancellationToken)
    {
        long key = unchecked((long)positionHash);
        // Played and imported games count together.
        var rows = await db.OpeningBook.AsNoTracking()
            .Where(b => b.PositionHash == key)
            .GroupBy(b => b.Move)
            .Select(g => new { Move = g.Key, Red = g.Sum(b => b.RedWins), Black = g.Sum(b => b.BlackWins), Draws = g.Sum(b => b.Draws) })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new BookMove(XqBook.DecodeMove(r.Move), r.Red, r.Black, r.Draws)).ToList();
    }

    /// <summary>Anonymous, stable key for a player: the first 16 bytes of SHA-256 of their browser id.</summary>
    public static byte[] PlayerKey(string playerId) => SHA256.HashData(Encoding.UTF8.GetBytes(playerId))[..16];
}
