using GameWeb.Games.Xiangqi;

namespace GameWeb.Tests;

public class NotationParserTests
{
    /// <summary>The sample game record supplied for the opening-book import.</summary>
    const string SampleGame = """
        * ==== 棋局开始 ====
        * 1.炮二平五马8进7
        * 2.马二进三车9平8
        * 3.车一平二卒7进1
        * 4.兵七进一炮2进4
        * 5.马八进七炮2平7
        * 6.马七进六马2进3
        * 7.车九平八车1平2
        * 8.炮八进四卒7进1
        * 9.仕四进五炮8进1
        * 10.兵五进一马7进6
        * 11.马六进五马3进5
        * 12.兵五进一马6进8
        * 13.车二进四卒7平8
        * 14.炮八平五车2进9
        * 15.兵五平四炮7平5
        * 16.马三进五将5进1
        * 17.马五进六将5平4
        * 18.炮五平六将4平5
        * 19.马六进七将5进1
        * 20.马七进六将5退1
        * 21.马六退七将5进1
        * 22.炮五退二车2退2
        * 23.兵四进一卒8平7
        * 24.兵四平五将5平6
        * 25.马七进六炮8进6
        * 26.相三进一车8进3
        * 27.兵五进一象7进5
        * 28.炮六进五象5退7
        * 29.炮五进三
        """;

    [Fact]
    public void SampleGame_ReplaysEveryMoveLegally()
    {
        var parsed = XqNotationParser.ParseGame(SampleGame);
        Assert.Null(parsed.Error);
        Assert.Equal(57, parsed.Notations.Count);
        Assert.Equal(57, parsed.Moves.Count);
        Assert.Equal(1, parsed.Resolved);   // 18. 炮五平六: two cannons on file 5, no 前/后
        var g = parsed.Game;
        Console.WriteLine($"Sample game: {g.History.Count} plies, result {g.Result} ({g.Reason}), " +
                          $"black in check: {g.Position.InCheck(XqSide.Black)}, black legal moves: {g.Position.LegalMoves().Count}");
        Console.WriteLine("Final position: " + g.Position.ToFen());
    }

    [Theory]
    [InlineData("炮二平五", "h2e2")]
    [InlineData("砲二平五", "h2e2")]   // Traditional cannon
    [InlineData("炮２平５", "h2e2")]    // digit style doesn't matter: still only one legal reading
    public void Parse_FirstMove(string written, string? expected)
    {
        var move = XqNotationParser.Parse(XqPosition.Start(), written, out _);
        Assert.Equal(expected is null ? null : XqMove.Parse(expected), move);
    }

    [Fact]
    public void Parse_TraditionalAndWideDigitBlackMoves()
    {
        var pos = XqPosition.Start();
        pos.Make(XqMove.Parse("h2e2"));
        Assert.Equal(XqMove.Parse("h9g7"), XqNotationParser.Parse(pos, "馬８進７", out _));
        Assert.Equal(XqMove.Parse("h9g7"), XqNotationParser.Parse(pos, "马8进7", out _));
    }

    [Fact]
    public void Parse_FrontAndRearPieces()
    {
        // Two red chariots on the a-file (Red's file 9): a3 is front, a0 is rear.
        var pos = XqPosition.FromFen("3k5/9/9/9/9/9/R8/9/9/R3K4 w - - 0 1");
        Assert.Equal(XqMove.Parse("a3a5"), XqNotationParser.Parse(pos, "前车进二", out _));
        Assert.Equal(XqMove.Parse("a0a1"), XqNotationParser.Parse(pos, "后车进一", out _));
        Assert.Equal(XqMove.Parse("a0a1"), XqNotationParser.Parse(pos, "後俥進一", out _));
    }

    [Fact]
    public void ParseGame_ReportsTheFirstBadMove()
    {
        var parsed = XqNotationParser.ParseGame("1.炮二平五马8进7 2.车一进九");
        Assert.Equal(2, parsed.Moves.Count);
        Assert.Contains("Move 3 (车一进九)", parsed.Error);
    }
}
