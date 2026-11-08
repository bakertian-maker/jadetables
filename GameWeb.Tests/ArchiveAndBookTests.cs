using GameWeb.Data;
using GameWeb.Games.Archive;
using GameWeb.Games.Rooms;
using GameWeb.Games.Xiangqi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GameWeb.Tests;

public class ZobristTests
{
    [Fact]
    public void StartPositionHash_NeverChanges()
    {
        // The opening book in the database is keyed by these hashes. If this fails, the hash
        // function changed and every stored book position would silently stop matching.
        Assert.Equal(0x0AABDADF8B60AC71UL, XqZobrist.Hash(XqPosition.Start()));
    }

    [Fact]
    public void IncrementalHash_MatchesFullRecompute()
    {
        var pos = XqPosition.Start();
        ulong hash = XqZobrist.Hash(pos);
        var rng = new Random(3);
        for (int i = 0; i < 60; i++)
        {
            var moves = pos.LegalMoves();
            if (moves.Count == 0) break;
            var m = moves[rng.Next(moves.Count)];
            hash = XqZobrist.After(hash, pos, m);
            pos.Make(m);
            Assert.Equal(XqZobrist.Hash(pos), hash);
        }
    }
}

public class OpeningBookTests
{
    static readonly string[] Opening =
        ["h2e2", "h9g7", "h0g2", "i9h9", "i0h0", "b9c7", "b0c2", "g6g5", "a0b0", "b7b3", "c3c4", "a9b9"];

    static XqGame PlayOpening(int plies)
    {
        var game = new XqGame();
        foreach (var m in Opening.Take(plies)) Assert.True(game.TryMove(XqMove.Parse(m)), m);
        return game;
    }

    [Fact]
    public void Entries_TakeTheOpeningOfAFinishedGame()
    {
        var game = PlayOpening(12);
        game.Resign(XqSide.Black);
        var entries = XqBook.Entries(game, _ => true);
        Assert.Equal(XqBook.MaxPlies, entries.Count);
        Assert.Equal(XqZobrist.Hash(XqPosition.Start()), entries[0].Hash);
        var first = XqMove.Parse("h2e2");
        Assert.Equal(first.From * 90 + first.To, entries[0].Move);
        Assert.Equal(first, XqBook.DecodeMove(entries[0].Move));
    }

    [Fact]
    public void Entries_SkipUntrustedSides_ShortGames_AndTimeouts()
    {
        var game = PlayOpening(12);
        game.Resign(XqSide.Black);
        Assert.Equal(6, XqBook.Entries(game, side => side == XqSide.Red).Count);

        var shortGame = PlayOpening(4);
        shortGame.Resign(XqSide.Red);
        Assert.Empty(XqBook.Entries(shortGame, _ => true));

        var timedOut = PlayOpening(12);
        timedOut.LoseOnTime(XqSide.Red);
        Assert.Empty(XqBook.Entries(timedOut, _ => true));

        Assert.Empty(XqBook.Entries(PlayOpening(12), _ => true)); // not finished
    }

    [Fact]
    public void Pick_UsesOnlyLegalWellTestedGoodMoves()
    {
        var pos = XqPosition.Start();
        var good = new BookMove(XqMove.Parse("h2e2"), RedWins: 8, BlackWins: 2, Draws: 0);
        var bad = new BookMove(XqMove.Parse("b2b9"), RedWins: 1, BlackWins: 9, Draws: 0);
        var rare = new BookMove(XqMove.Parse("h0g2"), RedWins: 2, BlackWins: 0, Draws: 0); // too few games
        var illegal = new BookMove(XqMove.Parse("h9g7"), RedWins: 50, BlackWins: 0, Draws: 0); // Black's move
        var rng = new Random(1);
        for (int i = 0; i < 20; i++)
            Assert.Equal(good.Move, XqBook.Pick(pos, [good, bad, rare, illegal], XqLevel.Difficult, rng));
        Assert.Null(XqBook.Pick(pos, [bad, rare], XqLevel.Medium, rng));
        Assert.Null(XqBook.Pick(pos, [good], XqLevel.Beginner, rng)); // beginners don't use the book
    }

    [Fact]
    public void BookMoveScore_IsFromTheMoversPointOfView()
    {
        var m = new BookMove(XqMove.Parse("h2e2"), RedWins: 6, BlackWins: 2, Draws: 2);
        Assert.Equal(0.7, m.ScoreFor(XqSide.Red), 3);
        Assert.Equal(0.3, m.ScoreFor(XqSide.Black), 3);
    }
}

public class RoomArchiveTests
{
    sealed class CapturingArchive : IGameArchive
    {
        public List<FinishedGame> Saved { get; } = [];
        public void Save(FinishedGame game) { lock (Saved) Saved.Add(game); }
    }

    [Fact]
    public void XiangqiRoom_SavesEachFinishedGameOnce_WithItsOpening()
    {
        var archive = new CapturingArchive();
        using var room = new XiangqiRoom("XQARC", new XqRoomSettings()) { Archive = archive };
        room.Connect("host", "Host");
        room.Connect("guest", "Guest");
        room.Start("host");

        Assert.True(room.Resign("guest"));
        Assert.False(room.Resign("guest"));          // already over: nothing more to save
        var first = Assert.Single(archive.Saved);
        Assert.Equal(GameType.Xiangqi, first.Type);
        Assert.Equal((byte)XqResult.RedWins, first.Result);
        Assert.Equal((byte)XqEndReason.Resignation, first.Reason);
        Assert.Empty(first.BookMoves);                // too short for the book
        Assert.Equal(["host", "guest"], first.Players.Select(p => p.PlayerId));

        // Rematch: the guest is now Red. Play 10 half-moves, then Red resigns.
        Assert.True(room.Rematch("host"));
        string[] moves = ["h2e2", "h9g7", "h0g2", "i9h9", "i0h0", "b9c7", "b0c2", "g6g5", "a0b0", "b7b3"];
        for (int i = 0; i < moves.Length; i++)
            Assert.True(room.Move(i % 2 == 0 ? "guest" : "host", XqMove.Parse(moves[i])), moves[i]);
        room.Resign("guest");

        Assert.Equal(2, archive.Saved.Count);
        var second = archive.Saved[1];
        Assert.Equal(10, second.PlyCount);
        Assert.Equal(string.Join(' ', moves), second.Moves);
        Assert.Equal(10, second.BookMoves.Count);
        Assert.Equal((byte)XqResult.BlackWins, second.Result);
    }

    [Fact]
    public void RoomService_GivesRoomsTheArchiveAndBook()
    {
        var archive = new CapturingArchive();
        using var service = new RoomService(Microsoft.Extensions.Logging.Abstractions.NullLogger<RoomService>.Instance, archive);
        var room = service.TryCreate(c => new XiangqiRoom(c, new XqRoomSettings()))!;
        Assert.Same(archive, room.Archive);
    }
}

/// <summary>
/// Runs against the developer's SQL Server (connection string from GameWeb's user secrets or the
/// ConnectionStrings__Games environment variable) inside a transaction that is rolled back, so it
/// leaves no data behind. Without a database (e.g. in CI) it does nothing.
/// </summary>
public class SqlGameStoreIntegrationTests
{
    const string UserSecretsId = "1ae01199-b626-4a8b-acae-e04d3370f059";

    static string? ConnectionString() => new ConfigurationBuilder()
        .AddUserSecrets(UserSecretsId)
        .AddEnvironmentVariables()
        .Build()
        .GetConnectionString("Games");

    [Fact]
    public async Task SavesGames_AndRecordsTheBook_WithTheStoredProcedure()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) { Console.WriteLine("SQL integration test skipped: no connection string"); return; }
        await using var db = new GamesDbContext(new DbContextOptionsBuilder<GamesDbContext>().UseSqlServer(cs).Options);
        if (!await db.Database.CanConnectAsync() || (await db.Database.GetPendingMigrationsAsync()).Any())
        {
            Console.WriteLine("SQL integration test skipped: database unreachable or not migrated");
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync();

        db.Games.Add(new GameRecord
        {
            GameType = GameType.Xiangqi, RoomCode = "TEST1", StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow,
            Result = 1, Reason = 1, PlyCount = 2, Moves = "h2e2 h9g7",
            Players = [new GamePlayerRecord { Seat = 0, Side = 0, PlayerKey = SqlGameStore.PlayerKey("p1") },
                       new GamePlayerRecord { Seat = 1, Side = 1, IsBot = true, BotLevel = 2 }],
        });
        await db.SaveChangesAsync();
        var saved = await db.Games.Include(g => g.Players).SingleAsync(g => g.RoomCode == "TEST1");
        Assert.Equal("h2e2 h9g7", saved.Moves);
        Assert.Equal(2, saved.Players.Count);
        Assert.Equal(16, saved.Players[0].PlayerKey!.Length);

        // A position unlikely to exist in the real book, so counts start from zero.
        ulong hash = 0xFEEDFACE00000001UL;
        var move = XqMove.Parse("h2e2");
        int encoded = move.From * 90 + move.To;
        await SqlGameStore.RecordBookAsync(db, [(hash, encoded)], result: 1);   // Red wins
        await SqlGameStore.RecordBookAsync(db, [(hash, encoded)], result: 3);   // draw
        await SqlGameStore.RecordBookAsync(db, [(hash, encoded)], result: 1);   // Red wins

        var entry = Assert.Single(await SqlGameStore.QueryBookAsync(db, hash, CancellationToken.None));
        Assert.Equal(move, entry.Move);
        Assert.Equal((2, 0, 1), (entry.RedWins, entry.BlackWins, entry.Draws));

        await tx.RollbackAsync();
        Console.WriteLine("SQL integration test ran against SQL Server and rolled back");
    }
}
