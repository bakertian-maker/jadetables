using System.Data;
using GameWeb.Games.Xiangqi;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace GameWeb.Data;

public sealed class ImportReport
{
    public int Files { get; set; }
    public int Imported { get; set; }
    public int Duplicates { get; set; }
    public List<(string File, string Problem)> Rejected { get; } = [];
    /// <summary>Moves written without 前/后 that fitted two pieces and were settled by looking ahead.</summary>
    public int AmbiguousMovesResolved { get; set; }
    public int TooShortForBook { get; set; }
    public int BookRows { get; set; }
    public int RedWins { get; set; }
    public int BlackWins { get; set; }
    public int Draws { get; set; }
}

/// <summary>A game to store: from a record file or from self-play.</summary>
public sealed record NewGame(string? FileName, string? OpeningName, XqResult Result, IReadOnlyList<XqMove> Moves)
{
    public string MovesText => string.Join(' ', Moves.Select(m => m.ToString()));
    public byte[] Hash() => XqRecordFile.GameHash(Result, MovesText);
}

/// <summary>
/// Stores Xiangqi games from outside the site (record files, self-play) in ImportedGames and adds
/// their openings to the book under their own <see cref="BookSource"/>. Each batch is one transaction,
/// so a game is never saved without its book counts; the same game (moves and result) is stored once.
/// </summary>
public sealed class XiangqiImporter(GamesDbContext db)
{
    public const int BatchSize = 1000;

    HashSet<string>? _known;

    /// <summary>Loads the hashes of games already stored, for duplicate checks.</summary>
    public async Task LoadKnownGamesAsync(CancellationToken ct = default) =>
        _known = (await db.ImportedGames.AsNoTracking().Select(g => g.ContentHash).ToListAsync(ct))
            .Select(Convert.ToHexString).ToHashSet();

    /// <summary>True (and remembers it) if this game isn't stored yet and hasn't been seen in this run.</summary>
    public bool IsNew(byte[] hash) => (_known ?? throw new InvalidOperationException("Call LoadKnownGamesAsync first"))
        .Add(Convert.ToHexString(hash));

    /// <summary>Reads, checks and stores game record files.</summary>
    public async Task<ImportReport> ImportAsync(IEnumerable<string> files, bool dryRun, TextWriter? progress = null, CancellationToken ct = default)
    {
        var report = new ImportReport();
        await LoadKnownGamesAsync(ct);
        var batch = new List<NewGame>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            report.Files++;
            XqRecordFile record;
            try
            {
                record = XqRecordFile.Parse(XqRecordFile.ReadText(await File.ReadAllBytesAsync(file, ct)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Rejected.Add((file, "could not read file: " + ex.Message));
                continue;
            }
            if (record.Problem is not null)
            {
                report.Rejected.Add((file, record.Problem));
                continue;
            }
            if (!IsNew(record.ContentHash()))
            {
                report.Duplicates++;
                continue;
            }
            report.AmbiguousMovesResolved += record.Parsed.Resolved;
            batch.Add(new NewGame(Path.GetFileName(file), record.OpeningName, record.Result, record.Moves));
            if (batch.Count >= BatchSize)
            {
                await SaveAsync(batch, BookSource.Imported, dryRun, report, ct);
                batch.Clear();
                progress?.WriteLine($"  {report.Files} files read, {report.Imported} games {(dryRun ? "ready" : "imported")}...");
            }
        }
        await SaveAsync(batch, BookSource.Imported, dryRun, report, ct);
        return report;
    }

    /// <summary>Stores a batch of games and adds their openings to the book under <paramref name="source"/>.</summary>
    public async Task SaveAsync(IReadOnlyList<NewGame> games, BookSource source, bool dryRun, ImportReport report, CancellationToken ct = default)
    {
        if (games.Count == 0) return;

        // Add up the book counts for the whole batch: one row per (position, move).
        var counts = new Dictionary<(ulong Hash, int Move), (int Red, int Black, int Draw)>();
        foreach (var game in games)
        {
            switch (game.Result)
            {
                case XqResult.RedWins: report.RedWins++; break;
                case XqResult.BlackWins: report.BlackWins++; break;
                default: report.Draws++; break;
            }
            if (game.Moves.Count < XqBook.MinGamePlies)
            {
                report.TooShortForBook++;
                continue;
            }
            foreach (var key in XqBook.OpeningEntries(game.Moves).Distinct())
            {
                counts.TryGetValue(key, out var c);
                counts[key] = game.Result switch
                {
                    XqResult.RedWins => (c.Red + 1, c.Black, c.Draw),
                    XqResult.BlackWins => (c.Red, c.Black + 1, c.Draw),
                    _ => (c.Red, c.Black, c.Draw + 1),
                };
            }
        }

        if (!dryRun)
        {
            // Use the caller's transaction if there is one (tests roll back); otherwise one per batch.
            bool ownTransaction = db.Database.CurrentTransaction is null;
            await using var tx = ownTransaction ? await db.Database.BeginTransactionAsync(ct) : null;

            var now = DateTime.UtcNow;
            db.ImportedGames.AddRange(games.Select(g => new ImportedGameRecord
            {
                Source = source,
                ContentHash = g.Hash(),
                FileName = g.FileName is { } f ? Truncate(f, 260) : null,
                OpeningName = g.OpeningName is { } name ? Truncate(name, 100) : null,
                Result = (byte)g.Result,
                PlyCount = (short)g.Moves.Count,
                Moves = g.MovesText,
                ImportedAt = now,
            }));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            await AddBookCountsAsync(db, source, counts, ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }

        report.Imported += games.Count;
        report.BookRows += counts.Count;
    }

    /// <summary>One call to dbo.Book_AddCounts with the batch's counts as a table-valued parameter.</summary>
    internal static async Task AddBookCountsAsync(GamesDbContext db, BookSource source,
        IReadOnlyDictionary<(ulong Hash, int Move), (int Red, int Black, int Draw)> counts, CancellationToken ct)
    {
        if (counts.Count == 0) return;
        var table = new DataTable();
        table.Columns.Add("PositionHash", typeof(long));
        table.Columns.Add("Move", typeof(short));
        table.Columns.Add("RedWins", typeof(int));
        table.Columns.Add("BlackWins", typeof(int));
        table.Columns.Add("Draws", typeof(int));
        foreach (var ((hash, move), (red, black, draw)) in counts)
            table.Rows.Add(unchecked((long)hash), (short)move, red, black, draw);

        await db.Database.ExecuteSqlRawAsync(
            "EXEC dbo.Book_AddCounts @Source, @Counts",
            [
                new SqlParameter("@Source", SqlDbType.TinyInt) { Value = (byte)source },
                new SqlParameter("@Counts", SqlDbType.Structured) { TypeName = "dbo.BookCountList", Value = table },
            ],
            ct);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
