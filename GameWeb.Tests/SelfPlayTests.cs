using GameWeb.Data;
using GameWeb.Games.Xiangqi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GameWeb.Tests;

public class SelfPlayTests
{
    static readonly SelfPlaySettings Fast = new() { MoveBudget = TimeSpan.FromMilliseconds(20), MaxPlies = 60 };

    [Fact]
    public void SelfPlayGame_IsLegal_AndHasAResult()
    {
        var r = XqSelfPlay.Play(Fast, new Random(1));
        Assert.NotEqual(XqResult.Ongoing, r.Result);
        Assert.InRange(r.Game.History.Count, 1, Fast.MaxPlies);
        // Replaying the moves from the start reproduces the game: every move was legal.
        var replay = new XqGame();
        foreach (var m in r.Moves) Assert.True(replay.TryMove(m));
        if (r.Ending == SelfPlayEnding.Rules) Assert.Equal(r.Game.Result, r.Result);
    }

    [Fact]
    public void MoveLimit_EndsLongGames()
    {
        var shortLimit = new SelfPlaySettings { MoveBudget = TimeSpan.FromMilliseconds(20), MaxPlies = 12, ResignScore = 100_000 };
        var r = XqSelfPlay.Play(shortLimit, new Random(2));
        Assert.Equal(12, r.Game.History.Count);
        Assert.Contains(r.Ending, new[] { SelfPlayEnding.MoveLimitWin, SelfPlayEnding.MoveLimitDraw });
    }

    [Fact]
    public void Variety_ProducesDifferentOpenings()
    {
        var openings = Enumerable.Range(0, 6)
            .Select(seed =>
            {
                var game = new XqGame();
                var rng = new Random(seed);
                for (int i = 0; i < 6; i++)
                    game.TryMove(XqBot.Analyse(game.Position, XqLevel.Difficult, rng, TimeSpan.FromMilliseconds(40), variety: 30).Move);
                return string.Join(' ', game.History.Select(h => h.Move));
            })
            .ToHashSet();
        Assert.True(openings.Count >= 3, $"only {openings.Count} distinct openings in 6 games");
    }

    [Fact]
    public void Openings_AreAllLegal_AndPickedByWeight()
    {
        var settings = new SelfPlaySettings();
        foreach (var (moves, _) in settings.Openings)
        {
            var game = new XqGame();
            foreach (var written in moves.Split(' '))
                Assert.True(XqNotationParser.Parse(game.Position, written, out _) is XqMove m && game.TryMove(m), $"{moves}: {written}");
        }
        var rng = new Random(9);
        var picks = Enumerable.Range(0, 1400).Select(_ => string.Join(' ', settings.PickOpening(rng))).ToList();
        // 中炮对屏风马 has weight 3 of 14: about 300 of 1400.
        Assert.InRange(picks.Count(p => p == "炮二平五 马8进7"), 230, 370);
        Assert.Equal(settings.Openings.Count, picks.Distinct().Count());
    }

    [Fact]
    public void SelfPlayGames_StartWithAMainstreamOpening()
    {
        var settings = new SelfPlaySettings { MoveBudget = TimeSpan.FromMilliseconds(20), MaxPlies = 4 };
        var starts = settings.Openings.Select(o => o.Moves).ToHashSet();
        for (int seed = 0; seed < 5; seed++)
        {
            var r = XqSelfPlay.Play(settings, new Random(seed));
            var firstTwo = r.Game.History.Take(2).Select(h => h.Move).ToList();
            var expected = settings.Openings.Select(o => o.Moves.Split(' ')).Select(w =>
            {
                var g = new XqGame();
                return w.Select(x => { var m = XqNotationParser.Parse(g.Position, x, out _)!.Value; g.TryMove(m); return m; }).ToList();
            });
            Assert.Contains(expected, e => e.SequenceEqual(firstTwo));
        }
    }

    [Fact]
    public void Analyse_ReturnsTheMoveWithItsScore()
    {
        // Red can take an undefended black chariot: the score should show a big gain.
        var pos = XqPosition.FromFen("3k5/9/9/9/4r4/9/4P4/4C4/9/5K3 w - - 0 1");
        var a = XqBot.Analyse(pos, XqLevel.Difficult, budget: TimeSpan.FromMilliseconds(200));
        Assert.Equal(XqMove.Parse("e2e5"), a.Move);
        Assert.True(a.Score > 500, $"score {a.Score}");
    }
}

[Collection(DatabaseCollection.Name)]
public class SelfPlayStorageIntegrationTests
{
    const string UserSecretsId = "1ae01199-b626-4a8b-acae-e04d3370f059";

    [Fact]
    public async Task SelfPlayGames_AreStoredUnderTheirOwnSource()
    {
        var cs = new ConfigurationBuilder().AddUserSecrets(UserSecretsId).AddEnvironmentVariables().Build().GetConnectionString("Games");
        if (string.IsNullOrWhiteSpace(cs)) { Console.WriteLine("Self-play storage test skipped: no connection string"); return; }
        await using var db = new GamesDbContext(new DbContextOptionsBuilder<GamesDbContext>().UseSqlServer(cs).Options);
        if (!await db.Database.CanConnectAsync() || (await db.Database.GetPendingMigrationsAsync()).Any())
        {
            Console.WriteLine("Self-play storage test skipped: database unreachable or not migrated");
            return;
        }

        var played = XqSelfPlay.Play(new SelfPlaySettings { MoveBudget = TimeSpan.FromMilliseconds(20), MaxPlies = 40 }, new Random(5));
        var importer = new XiangqiImporter(db);
        await importer.LoadKnownGamesAsync();

        await using var tx = await db.Database.BeginTransactionAsync();
        var game = new NewGame("selfplay test", null, played.Result, played.Moves);
        Assert.True(importer.IsNew(game.Hash()));
        var report = new ImportReport();
        await importer.SaveAsync([game], BookSource.SelfPlay, dryRun: false, report);

        var stored = await db.ImportedGames.AsNoTracking().SingleAsync(g => g.FileName == "selfplay test");
        Assert.Equal(BookSource.SelfPlay, stored.Source);
        Assert.Equal(game.MovesText, stored.Moves);
        long start = unchecked((long)XqZobrist.Hash(XqPosition.Start()));
        Assert.True(await db.OpeningBook.AnyAsync(b => b.PositionHash == start && b.Source == BookSource.SelfPlay));
        Assert.False(importer.IsNew(game.Hash()));   // a repeat in the same run is a duplicate

        await tx.RollbackAsync();
        Console.WriteLine("Self-play storage test ran against SQL Server and rolled back");
    }
}
