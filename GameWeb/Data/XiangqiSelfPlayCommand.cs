using System.Diagnostics;
using GameWeb.Games.Xiangqi;
using Microsoft.EntityFrameworkCore;

namespace GameWeb.Data;

/// <summary>
/// Command line: the computer plays itself and the games go into the opening book (source: self-play).
/// <code>
/// dotnet run --project GameWeb -- selfplay-xiangqi [--games 100] [--budget 300] [--parallel N] [--dry-run]
/// </code>
/// --budget is thinking time per move in milliseconds; --parallel is how many games run at once
/// (default: CPU cores minus one). Games are saved every round, so Ctrl+C keeps what is finished.
/// </summary>
public static class XiangqiSelfPlayCommand
{
    public const string Name = "selfplay-xiangqi";

    public static async Task<int> RunAsync(string[] args, IConfiguration config)
    {
        int games = IntOption(args, "--games", 100);
        int budgetMs = IntOption(args, "--budget", 300);
        int parallel = IntOption(args, "--parallel", Math.Max(1, Environment.ProcessorCount - 1));
        bool dryRun = args.Contains("--dry-run");
        if (games < 1 || budgetMs < 20 || parallel < 1)
        {
            Console.Error.WriteLine($"Usage: {Name} [--games N] [--budget ms (>= 20)] [--parallel N] [--dry-run]");
            return 2;
        }

        var cs = config.GetConnectionString("Games");
        if (string.IsNullOrWhiteSpace(cs))
        {
            Console.Error.WriteLine("No ConnectionStrings:Games configured (user secrets or environment).");
            return 2;
        }
        await using var db = new GamesDbContext(new DbContextOptionsBuilder<GamesDbContext>()
            .UseSqlServer(cs, sql => sql.CommandTimeout(120)).Options);
        if (config.GetValue<bool>("Database:MigrateOnStartup")) await db.Database.MigrateAsync();

        var importer = new XiangqiImporter(db);
        await importer.LoadKnownGamesAsync();
        var settings = new SelfPlaySettings { MoveBudget = TimeSpan.FromMilliseconds(budgetMs) };
        var report = new ImportReport();
        var endings = new Dictionary<SelfPlayEnding, int>();
        long totalPlies = 0;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
            Console.WriteLine("Stopping after the current round; finished games will be saved...");
        };

        Console.WriteLine($"Self-play: {games} games, {budgetMs} ms per move, {parallel} at a time{(dryRun ? ", dry run" : "")}.");
        var sw = Stopwatch.StartNew();
        int played = 0, seed = Environment.TickCount;

        while (played < games && !stop.IsCancellationRequested)
        {
            int round = Math.Min(parallel, games - played);
            var results = new SelfPlayGame[round];
            int roundSeed = seed + played;
            Parallel.For(0, round, new ParallelOptions { MaxDegreeOfParallelism = parallel },
                i => results[i] = XqSelfPlay.Play(settings, new Random(roundSeed + i)));
            played += round;

            var batch = new List<NewGame>();
            foreach (var r in results)
            {
                endings[r.Ending] = endings.GetValueOrDefault(r.Ending) + 1;
                totalPlies += r.Game.History.Count;
                var game = new NewGame($"selfplay {budgetMs}ms", null, r.Result, r.Moves);
                if (importer.IsNew(game.Hash())) batch.Add(game);
                else report.Duplicates++;
            }
            await importer.SaveAsync(batch, BookSource.SelfPlay, dryRun, report);

            double perGame = sw.Elapsed.TotalSeconds / played;
            var left = TimeSpan.FromSeconds(perGame * (games - played));
            Console.WriteLine($"  {played}/{games} games · Red {report.RedWins} / Black {report.BlackWins} / draw {report.Draws} · " +
                              $"avg {totalPlies / played} half-moves · {perGame:0.0} s/game · about {left:hh\\:mm\\:ss} left");
        }

        Console.WriteLine();
        Console.WriteLine(dryRun ? "Dry run: nothing was written." : "Self-play finished.");
        Console.WriteLine($"  Games played:      {played}");
        Console.WriteLine($"  Games saved:       {report.Imported}  (duplicates skipped: {report.Duplicates})");
        Console.WriteLine($"  Results:           Red {report.RedWins}, Black {report.BlackWins}, draws {report.Draws}");
        Console.WriteLine($"  How games ended:   {string.Join(", ", endings.OrderBy(e => e.Key).Select(e => $"{Describe(e.Key)} {e.Value}"))}");
        Console.WriteLine($"  Book rows {(dryRun ? "to add or update" : "added or updated")}: {report.BookRows}");
        Console.WriteLine($"  Time:              {sw.Elapsed:hh\\:mm\\:ss}");
        return 0;
    }

    static string Describe(SelfPlayEnding e) => e switch
    {
        SelfPlayEnding.Rules => "by the rules (mate, stalemate, 60-move draw)",
        SelfPlayEnding.Resignation => "resigned (clearly lost)",
        SelfPlayEnding.MoveLimitWin => "move limit, won on advantage",
        _ => "move limit, drawn",
    };

    static int IntOption(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }
}
