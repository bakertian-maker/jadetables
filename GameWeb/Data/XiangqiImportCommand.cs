using Microsoft.EntityFrameworkCore;

namespace GameWeb.Data;

/// <summary>
/// Command line: import Xiangqi game files into the opening book, then exit.
/// <code>
/// dotnet run --project GameWeb -- import-xiangqi &lt;file or folder&gt; [--dry-run]
/// </code>
/// A folder is searched recursively for *.txt files. --dry-run reads and checks everything and
/// prints the report without writing to the database.
/// </summary>
public static class XiangqiImportCommand
{
    public const string Name = "import-xiangqi";

    public static async Task<int> RunAsync(string[] args, IConfiguration config)
    {
        var paths = args.Where(a => !a.StartsWith("--")).ToList();
        bool dryRun = args.Contains("--dry-run");
        if (paths.Count == 0)
        {
            Console.Error.WriteLine($"Usage: {Name} <file or folder> [--dry-run]");
            return 2;
        }

        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.EnumerateFiles(path, "*.txt", SearchOption.AllDirectories).Order());
            else if (File.Exists(path)) files.Add(path);
            else
            {
                Console.Error.WriteLine($"Not found: {path}");
                return 2;
            }
        }

        var cs = config.GetConnectionString("Games");
        if (string.IsNullOrWhiteSpace(cs))
        {
            Console.Error.WriteLine("No ConnectionStrings:Games configured (user secrets or environment).");
            return 2;
        }

        // No retry strategy here: the importer uses its own transactions.
        await using var db = new GamesDbContext(new DbContextOptionsBuilder<GamesDbContext>()
            .UseSqlServer(cs, sql => sql.CommandTimeout(120)).Options);
        if (config.GetValue<bool>("Database:MigrateOnStartup")) await db.Database.MigrateAsync();

        Console.WriteLine($"{(dryRun ? "Checking" : "Importing")} {files.Count} file(s)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = await new XiangqiImporter(db).ImportAsync(files, dryRun, Console.Out);

        Console.WriteLine();
        Console.WriteLine(dryRun ? "Dry run: nothing was written." : "Import finished.");
        void Line(string label, object value) => Console.WriteLine($"  {label,-34}{value}");
        Line("Files read:", report.Files);
        Line(dryRun ? "Games to import:" : "Games imported:",
            $"{report.Imported}  (Red wins {report.RedWins}, Black wins {report.BlackWins}, draws {report.Draws})");
        Line("Already in the database:", report.Duplicates);
        Line("Rejected:", report.Rejected.Count);
        Line("Too short for the book:", report.TooShortForBook);
        Line("Moves missing 前/后 (resolved):", report.AmbiguousMovesResolved);
        Line(dryRun ? "Book rows to add or update:" : "Book rows added or updated:", report.BookRows);
        Line("Time:", $"{sw.Elapsed.TotalSeconds:0.0} s");
        foreach (var (file, problem) in report.Rejected.Take(50))
            Console.WriteLine($"  Rejected {Path.GetFileName(file)}: {problem}");
        if (report.Rejected.Count > 50) Console.WriteLine($"  ... and {report.Rejected.Count - 50} more");
        return 0;
    }
}
