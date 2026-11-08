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

/// <summary>
/// Imports Xiangqi game record files (one game per file, Chinese notation) into ImportedGames and
/// adds their openings to the book under <see cref="BookSource.Imported"/>.
/// Games are written in batches; each batch is one transaction, so a failure never leaves a game
/// saved without its book counts. The same game (same moves and result) is only imported once.
/// </summary>
public sealed class XiangqiImporter(GamesDbContext db)
{
    public const int BatchSize = 1000;

    public async Task<ImportReport> ImportAsync(IEnumerable<string> files, bool dryRun, TextWriter? progress = null, CancellationToken ct = default)
    {
        var report = new ImportReport();
        var known = (await db.ImportedGames.AsNoTracking().Select(g => g.ContentHash).ToListAsync(ct))
            .Select(Convert.ToHexString).ToHashSet();
        var batch = new List<(string File, XqRecordFile Record)>();

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
            if (!known.Add(Convert.ToHexString(record.ContentHash())))
            {
                report.Duplicates++;
                continue;
            }
            report.AmbiguousMovesResolved += record.Parsed.Resolved;
            batch.Add((file, record));
            if (batch.Count >= BatchSize)
            {
                await FlushAsync(batch, report, dryRun, ct);
                progress?.WriteLine($"  {report.Files} files read, {report.Imported} games {(dryRun ? "ready" : "imported")}...");
            }
        }
        await FlushAsync(batch, report, dryRun, ct);
        return report;
    }

    async Task FlushAsync(List<(string File, XqRecordFile Record)> batch, ImportReport report, bool dryRun, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        // Add up the book counts for the whole batch: one row per (position, move).
        var counts = new Dictionary<(ulong Hash, int Move), (int Red, int Black, int Draw)>();
        foreach (var (_, record) in batch)
        {
            switch (record.Result)
            {
                case XqResult.RedWins: report.RedWins++; break;
                case XqResult.BlackWins: report.BlackWins++; break;
                default: report.Draws++; break;
            }
            if (record.Moves.Count < XqBook.MinGamePlies)
            {
                report.TooShortForBook++;
                continue;
            }
            foreach (var key in XqBook.OpeningEntries(record.Moves).Distinct())
            {
                counts.TryGetValue(key, out var c);
                counts[key] = record.Result switch
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
            db.ImportedGames.AddRange(batch.Select(b => new ImportedGameRecord
            {
                ContentHash = b.Record.ContentHash(),
                FileName = Truncate(Path.GetFileName(b.File), 260),
                OpeningName = b.Record.OpeningName is { } name ? Truncate(name, 100) : null,
                Result = (byte)b.Record.Result,
                PlyCount = (short)b.Record.Moves.Count,
                Moves = b.Record.MovesText,
                ImportedAt = now,
            }));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            await AddBookCountsAsync(db, BookSource.Imported, counts, ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }

        report.Imported += batch.Count;
        report.BookRows += counts.Count;
        batch.Clear();
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
