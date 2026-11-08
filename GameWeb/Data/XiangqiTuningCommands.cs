using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GameWeb.Games.Xiangqi;
using Microsoft.EntityFrameworkCore;

namespace GameWeb.Data;

/// <summary>
/// Command line tools for tuning the Xiangqi evaluation:
/// <code>
/// tune-xiangqi [--iterations 600] [--out weights.json]     fit weights to all saved games
/// match-xiangqi --candidate weights.json [--games 200] [--budget 100] [--parallel N]
///                                                           play candidate vs the current evaluation
/// tune-xiangqi --adopt weights.json                         write the weights into XqEvalTuned.cs
/// </code>
/// </summary>
public static class XiangqiTuningCommands
{
    public const string Tune = "tune-xiangqi";
    public const string Match = "match-xiangqi";

    public static async Task<int> TuneAsync(string[] args, IConfiguration config)
    {
        if (Option(args, "--adopt") is { } adoptFile) return Adopt(adoptFile);

        int iterations = int.TryParse(Option(args, "--iterations"), out var it) ? it : 600;
        string outFile = Path.GetFullPath(Option(args, "--out") ?? "xiangqi-tuned-weights.json");
        var cs = config.GetConnectionString("Games");
        if (string.IsNullOrWhiteSpace(cs)) { Console.Error.WriteLine("No ConnectionStrings:Games configured."); return 2; }

        await using var db = new GamesDbContext(new DbContextOptionsBuilder<GamesDbContext>().UseSqlServer(cs).Options);
        var sw = Stopwatch.StartNew();
        var games = new List<(int, IReadOnlyList<XqMove>, XqResult)>();
        int id = 0;
        foreach (var g in await db.ImportedGames.AsNoTracking().Select(g => new { g.Moves, g.Result }).ToListAsync())
            games.Add((id++, ParseMoves(g.Moves), (XqResult)g.Result));
        foreach (var g in await db.Games.AsNoTracking().Where(g => g.GameType == Games.Archive.GameType.Xiangqi && g.Moves != null)
                     .Select(g => new { g.Moves, g.Result }).ToListAsync())
            games.Add((id++, ParseMoves(g.Moves!), (XqResult)g.Result));
        Console.WriteLine($"Loaded {games.Count} games. Extracting quiet positions...");

        var start = XqEval.Default;
        var samples = XqTuner.Samples(games, start);
        Console.WriteLine($"{samples.Count} positions ({sw.Elapsed.TotalSeconds:0} s). Tuning {iterations} iterations...");
        var report = XqTuner.Tune(samples, start.Weights.ToArray(), iterations, frozen: new HashSet<int> { 0, Pieces.General },
            progress: Console.Out);

        await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(report.Weights));
        Console.WriteLine();
        Console.WriteLine($"  Positions:   {report.TrainSamples} for fitting, {report.TestSamples} held out (by game)");
        Console.WriteLine($"  K:           {report.K:0.000}");
        Console.WriteLine($"  Fit error:   {report.TrainErrorBefore:0.000000} -> {report.TrainErrorAfter:0.000000}");
        Console.WriteLine($"  Test error:  {report.TestErrorBefore:0.000000} -> {report.TestErrorAfter:0.000000}" +
                          (report.TestErrorAfter < report.TestErrorBefore ? "  (better on unseen games)" : "  (no better on unseen games)"));
        Console.WriteLine("  Effective piece values (value + average square bonus), before -> after:");
        string[] names = ["", "General", "Advisor", "Elephant", "Horse", "Chariot", "Cannon", "Soldier"];
        for (int type = 2; type <= 7; type++)
            Console.WriteLine($"    {names[type],-9} {Effective(start.Weights, type),5:0} -> {Effective(report.Weights, type),5:0}");
        Console.WriteLine($"  Weights saved to {outFile}");
        Console.WriteLine($"  Next: {Match} --candidate \"{outFile}\"");
        return 0;
    }

    public static Task<int> MatchAsync(string[] args)
    {
        var candidateFile = Option(args, "--candidate");
        if (candidateFile is null || !File.Exists(candidateFile))
        {
            Console.Error.WriteLine($"Usage: {Match} --candidate weights.json [--games 200] [--budget 100] [--parallel N]");
            return Task.FromResult(2);
        }
        var candidate = new XqEval(JsonSerializer.Deserialize<int[]>(File.ReadAllText(candidateFile))!);
        var baseline = XqEval.Default;
        int games = int.TryParse(Option(args, "--games"), out var g) ? Math.Max(2, g / 2 * 2) : 200;
        int budget = int.TryParse(Option(args, "--budget"), out var b) ? b : 100;
        int parallel = int.TryParse(Option(args, "--parallel"), out var p) ? p : Math.Max(1, Environment.ProcessorCount - 1);
        var settings = new SelfPlaySettings { MoveBudget = TimeSpan.FromMilliseconds(budget) };

        Console.WriteLine($"Match: candidate vs current evaluation, {games} games ({games / 2} openings, each played from both sides), {budget} ms per move.");
        var sw = Stopwatch.StartNew();
        var scores = new double[games]; // candidate's score per game
        int done = 0;
        Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = parallel }, i =>
        {
            int pair = i / 2;
            bool candidateIsRed = i % 2 == 0;
            var r = XqSelfPlay.Play(settings, new Random(1000 + pair),
                candidateIsRed ? candidate : baseline, candidateIsRed ? baseline : candidate);
            scores[i] = r.Result switch
            {
                XqResult.Draw => 0.5,
                XqResult.RedWins => candidateIsRed ? 1 : 0,
                _ => candidateIsRed ? 0 : 1,
            };
            int n = Interlocked.Increment(ref done);
            if (n % Math.Max(1, games / 10) == 0) Console.WriteLine($"  {n}/{games} games, {sw.Elapsed:hh\\:mm\\:ss}");
        });

        int wins = scores.Count(s => s == 1), draws = scores.Count(s => s == 0.5), losses = scores.Count(s => s == 0);
        double score = scores.Average();
        double sd = Math.Sqrt(scores.Select(s => (s - score) * (s - score)).Sum() / (games - 1));
        double margin = 1.96 * sd / Math.Sqrt(games);
        static double Elo(double s) => s <= 0 ? double.NegativeInfinity : s >= 1 ? double.PositiveInfinity : -400 * Math.Log10(1 / s - 1);

        Console.WriteLine();
        Console.WriteLine($"  Candidate: {wins} wins, {draws} draws, {losses} losses  ->  score {score:P1}");
        Console.WriteLine($"  Elo difference: {Elo(score):+0;-0} (95% range {Elo(score - margin):+0;-0} to {Elo(score + margin):+0;-0})");
        Console.WriteLine(score - margin > 0.5 ? "  The candidate is stronger." :
                          score + margin < 0.5 ? "  The candidate is weaker." :
                          "  No clear difference yet: play more games to decide.");
        return Task.FromResult(0);
    }

    /// <summary>Writes the weights into Games/Xiangqi/XqEvalTuned.cs so the computer plays with them after a rebuild.</summary>
    static int Adopt(string weightsFile)
    {
        var weights = JsonSerializer.Deserialize<int[]>(File.ReadAllText(weightsFile))!;
        _ = new XqEval(weights); // validates the length
        var target = Path.Combine(Environment.CurrentDirectory, "Games", "Xiangqi", "XqEvalTuned.cs");
        if (!File.Exists(target))
        {
            Console.Error.WriteLine($"Not found: {target}. Run this from the GameWeb project folder (dotnet run --project GameWeb).");
            return 2;
        }
        var sb = new StringBuilder();
        sb.AppendLine("namespace GameWeb.Games.Xiangqi;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Tuned evaluation weights, written by the tune-xiangqi command on {DateTime.UtcNow:yyyy-MM-dd} from {Path.GetFileName(weightsFile)}.");
        sb.AppendLine("/// Layout: see XqEval (8 piece values, then 8 tables of 50 squares). Delete the array (set it to null)");
        sb.AppendLine("/// to go back to the hand-written evaluation.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class XqEvalTuned");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly int[]? Weights =");
        sb.AppendLine("    [");
        sb.AppendLine($"        {string.Join(", ", weights[..8])},");
        for (int type = 0; type < 8; type++)
            for (int rank = 0; rank < 10; rank++)
                sb.AppendLine($"        {string.Join(", ", weights.Skip(XqEval.PstOffset(type) + rank * 5).Take(5))},");
        sb.AppendLine("    ];");
        sb.AppendLine("}");
        File.WriteAllText(target, sb.ToString());
        Console.WriteLine($"Wrote {target}. Rebuild for the computer to use the tuned evaluation.");
        return 0;
    }

    static double Effective(IReadOnlyList<int> w, int type) =>
        w[type] + Enumerable.Range(0, XqEval.Squares).Average(i => w[XqEval.PstOffset(type) + i]);

    static List<XqMove> ParseMoves(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(XqMove.Parse).ToList();

    static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
