using GameWeb.Games.Xiangqi;

namespace GameWeb.Tests;

public class RepetitionTests
{
    static XqGame Play(string fen, params string[] moves)
    {
        var game = new XqGame(fen);
        foreach (var m in moves)
            Assert.True(game.TryMove(XqMove.Parse(m)), $"move {m} should be legal");
        return game;
    }

    static string[] Twice(params string[] cycle) => [.. cycle, .. cycle];

    [Fact]
    public void PerpetualCheck_Loses()
    {
        var game = Play("4k4/R8/9/9/9/9/9/9/9/3K5 w - - 0 1", Twice("a8a9", "e9e8", "a9a8", "e8e9"));
        Assert.Equal(XqResult.BlackWins, game.Result);
        Assert.Equal(XqEndReason.PerpetualCheck, game.Reason);
    }

    [Fact]
    public void RepetitionWithoutChecksOrChases_IsADraw()
    {
        var game = Play("1n1k5/9/9/9/9/9/9/9/9/1N2K4 w - - 0 1", Twice("b0c2", "b9c7", "c2b0", "c7b9"));
        Assert.Equal(XqResult.Draw, game.Result);
        Assert.Equal(XqEndReason.Repetition, game.Reason);
    }

    [Fact]
    public void TwoOccurrences_AreNotYetARepetition()
    {
        var game = Play("1n1k5/9/9/9/9/9/9/9/9/1N2K4 w - - 0 1", "b0c2", "b9c7", "c2b0", "c7b9", "b0c2", "b9c7", "c2b0");
        Assert.False(game.IsOver);
    }

    [Fact]
    public void PerpetualChase_OfAnUnprotectedPiece_Loses()
    {
        // The chariot keeps attacking the loose cannon along the rank; the cannon only runs.
        var game = Play("3k5/9/9/c8/8R/9/9/9/9/4K4 w - - 0 1", Twice("i5i6", "a6a5", "i6i5", "a5a6"));
        Assert.Equal(XqResult.BlackWins, game.Result);
        Assert.Equal(XqEndReason.PerpetualChase, game.Reason);
    }

    [Fact]
    public void Chase_Definition()
    {
        // Chariot attacks a loose cannon: a chase.
        Assert.True(XqRepetition.IsChasing(XqPosition.FromFen("3k5/9/9/c7R/9/9/9/9/9/4K4 b - - 0 1"), XqSide.Red));
        // The cannon is protected by its chariot and worth less than the attacker: not a chase.
        Assert.False(XqRepetition.IsChasing(XqPosition.FromFen("3k5/9/r8/c7R/9/9/9/9/9/4K4 b - - 0 1"), XqSide.Red));
        // A horse attacking a protected chariot is still a chase: the chariot is worth more.
        Assert.True(XqRepetition.IsChasing(XqPosition.FromFen("3k5/9/9/r8/2N6/9/9/9/9/4K4 b - - 0 1"), XqSide.Red));
        // Soldiers may chase freely.
        Assert.False(XqRepetition.IsChasing(XqPosition.FromFen("3k5/9/9/rP7/9/9/9/9/9/4K4 b - - 0 1"), XqSide.Red));
        // A soldier that hasn't crossed the river can't be chased.
        Assert.False(XqRepetition.IsChasing(XqPosition.FromFen("3k5/9/9/p7R/9/9/9/9/9/4K4 b - - 0 1"), XqSide.Red));
    }

    [Fact]
    public void MovesThatLoseByRepetition_CatchesTheCheckThatCompletesIt()
    {
        // Red has checked twice round the cycle; checking again lets Black repeat a third time.
        var game = Play("4k4/R8/9/9/9/9/9/9/9/3K5 w - - 0 1", "a8a9", "e9e8", "a9a8", "e8e9", "a8a9", "e9e8");
        Assert.Contains(XqMove.Parse("a9a8"), game.MovesThatLoseByRepetition());
        Assert.DoesNotContain(XqMove.Parse("a9b9"), game.MovesThatLoseByRepetition());
    }

    [Theory]
    [InlineData(XqLevel.Beginner)]
    [InlineData(XqLevel.Medium)]
    [InlineData(XqLevel.Difficult)]
    public void Computer_AvoidsLosingByPerpetualCheck(XqLevel level)
    {
        var game = Play("4k4/R8/9/9/9/9/9/9/9/3K5 w - - 0 1", "a8a9", "e9e8", "a9a8", "e8e9", "a8a9", "e9e8");
        var move = XqBot.ChooseMove(game.Position, level, new Random(1), TimeSpan.FromMilliseconds(300), XqBot.XqGameContext.Of(game));
        Assert.NotEqual(XqMove.Parse("a9a8"), move);
    }

    [Fact]
    public void Search_ScoresARepeatAsADraw_SoAWinningSideLooksElsewhere()
    {
        // Red is a chariot up. Going back to a position from earlier in the game would only draw,
        // so the engine's score stays clearly positive and it doesn't pick a repeating move.
        var game = Play("1n1k5/9/9/9/9/9/9/9/R8/1N2K4 w - - 0 1", "b0c2", "b9c7", "c2b0", "c7b9", "b0c2", "b9c7");
        var context = XqBot.XqGameContext.Of(game);
        var a = XqBot.Analyse(game.Position, XqLevel.Difficult, new Random(1), TimeSpan.FromMilliseconds(300), context: context);
        Assert.True(a.Score > 300);
        var after = game.Position.Clone();
        after.Make(a.Move);
        Assert.DoesNotContain(XqZobrist.Hash(after), context.Seen);
    }
}
