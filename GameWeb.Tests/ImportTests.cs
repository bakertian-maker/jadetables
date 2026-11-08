using System.Text;
using GameWeb.Data;
using GameWeb.Games.Xiangqi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GameWeb.Tests;

public class RecordFileTests
{
    static readonly string Sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "xiangqi-sample.txt"));

    [Fact]
    public void Sample_ReadsMetadataMovesAndResult()
    {
        var record = XqRecordFile.Parse(Sample);
        Assert.Null(record.Problem);
        Assert.Equal("中炮对进左马", record.OpeningName);
        Assert.Equal(57, record.StatedPlies);
        Assert.Equal(57, record.Moves.Count);
        Assert.Equal(XqResult.RedWins, record.StatedResult);
        Assert.Equal(XqResult.RedWins, record.Result);
        Assert.StartsWith("h2e2 h9g7 h0g2 i9h9", record.MovesText);
    }

    [Fact]
    public void ResultThatContradictsTheBoard_IsRejected()
    {
        var record = XqRecordFile.Parse(Sample.Replace("红先胜", "红先负"));
        Assert.Contains("棋局结果", record.Problem);
    }

    [Fact]
    public void WrongMoveCount_IsRejected()
    {
        var record = XqRecordFile.Parse(Sample.Replace("步数57", "步数50"));
        Assert.Contains("步数 says 50", record.Problem);
    }

    [Fact]
    public void MissingResult_TakesTheBoardResult_WhenTheGameEndsOnTheBoard()
    {
        var record = XqRecordFile.Parse(Sample.Replace("棋局结果红先胜", ""));
        Assert.Null(record.Problem);
        Assert.Equal(XqResult.RedWins, record.Result);
    }

    [Fact]
    public void MissingResult_InAnUnfinishedGame_IsRejected()
    {
        var unfinished = string.Join('\n', Sample.Split('\n').Where(l => !l.Contains("29.") && !l.StartsWith("棋局结果") && !l.StartsWith("步数")));
        var record = XqRecordFile.Parse(unfinished);
        Assert.Contains("no result", record.Problem);
    }

    [Fact]
    public void GbkEncodedFile_ReadsTheSame()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding("GB18030").GetBytes(Sample);
        Assert.Equal(XqRecordFile.Parse(Sample).MovesText, XqRecordFile.Parse(XqRecordFile.ReadText(gbk)).MovesText);
    }

    [Theory]
    [InlineData("红先胜", XqResult.RedWins)]
    [InlineData("红胜", XqResult.RedWins)]
    [InlineData("紅先勝", XqResult.RedWins)]
    [InlineData("黑胜", XqResult.BlackWins)]
    [InlineData("红先负", XqResult.BlackWins)]
    [InlineData("和棋", XqResult.Draw)]
    [InlineData("红先和", XqResult.Draw)]
    [InlineData("未知", null)]
    public void ResultText(string text, XqResult? expected) => Assert.Equal(expected, XqRecordFile.ParseResult(text));

    [Fact]
    public void ContentHash_IsTheSameForTheSameGame()
    {
        var a = XqRecordFile.Parse(Sample);
        var b = XqRecordFile.Parse("轮次 第3轮\n" + Sample); // different file, same game
        Assert.Equal(a.ContentHash(), b.ContentHash());
    }
}

/// <summary>Imports into the developer's SQL Server inside a rolled-back transaction (skipped without a database).</summary>
[Collection(DatabaseCollection.Name)]
public class ImporterIntegrationTests
{
    const string UserSecretsId = "1ae01199-b626-4a8b-acae-e04d3370f059";

    [Fact]
    public async Task Import_AddsGamesAndBook_SkipsDuplicates_RejectsBadFiles()
    {
        var cs = new ConfigurationBuilder().AddUserSecrets(UserSecretsId).AddEnvironmentVariables().Build().GetConnectionString("Games");
        if (string.IsNullOrWhiteSpace(cs)) { Console.WriteLine("Import integration test skipped: no connection string"); return; }
        await using var db = new GamesDbContext(new DbContextOptionsBuilder<GamesDbContext>().UseSqlServer(cs).Options);
        if (!await db.Database.CanConnectAsync() || (await db.Database.GetPendingMigrationsAsync()).Any())
        {
            Console.WriteLine("Import integration test skipped: database unreachable or not migrated");
            return;
        }

        var dir = Directory.CreateTempSubdirectory("xq-import-");
        try
        {
            // The first 40 half-moves of the sample game, stated as a draw: a game that won't already be in
            // the developer's database (the full sample may have been imported for real).
            var sampleLines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "xiangqi-sample.txt"));
            var moveLines = sampleLines.Where(l => l.StartsWith("* ") && !l.Contains("====")).Take(20);
            var sample = "棋局结果和棋\n开局中炮对进左马\n" + string.Join("\n", moveLines);
            File.WriteAllText(Path.Combine(dir.FullName, "a.txt"), sample);
            File.WriteAllText(Path.Combine(dir.FullName, "b-copy.txt"), sample);                       // same game again
            File.WriteAllText(Path.Combine(dir.FullName, "c-bad.txt"), "1.炮二平五马8进7 2.车一进九"); // illegal move
            var files = Directory.GetFiles(dir.FullName).Order().ToList();

            ulong startHash = XqZobrist.Hash(XqPosition.Start());
            var before = (await SqlGameStore.QueryBookAsync(db, startHash, default))
                .Where(m => m.Move == XqMove.Parse("h2e2")).Sum(m => m.Draws);

            await using var tx = await db.Database.BeginTransactionAsync();
            var report = await new XiangqiImporter(db).ImportAsync(files, dryRun: false);

            Assert.Equal(3, report.Files);
            Assert.Equal(1, report.Imported);
            Assert.Equal(1, report.Duplicates);
            Assert.Single(report.Rejected);
            Assert.Equal(1, report.AmbiguousMovesResolved);
            Assert.Equal(XqBook.MaxPlies, report.BookRows);

            var game = await db.ImportedGames.AsNoTracking().SingleAsync(g => g.FileName == "a.txt");
            Assert.Equal("中炮对进左马", game.OpeningName);
            Assert.Equal(40, game.PlyCount);
            Assert.Equal(BookSource.Imported, game.Source);

            // The first move is now in the book under the imported source, and lookups include it.
            var after = (await SqlGameStore.QueryBookAsync(db, startHash, default))
                .Where(m => m.Move == XqMove.Parse("h2e2")).Sum(m => m.Draws);
            Assert.Equal(before + 1, after);
            Assert.True(await db.OpeningBook.AnyAsync(b => b.PositionHash == unchecked((long)startHash) && b.Source == BookSource.Imported));

            // Importing the same file again finds it already there.
            var again = await new XiangqiImporter(db).ImportAsync(files.Take(1), dryRun: false);
            Assert.Equal((0, 1), (again.Imported, again.Duplicates));

            await tx.RollbackAsync();
            Console.WriteLine("Import integration test ran against SQL Server and rolled back");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
