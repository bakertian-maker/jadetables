using GameWeb.Games.Xiangqi;

namespace GameWeb.Tests;

public class XiangqiRulesTests
{
    static XqMove M(string s) => XqMove.Parse(s);

    static bool CanMove(string fen, string move) => XqPosition.FromFen(fen).LegalMoves().Contains(M(move));

    [Fact]
    public void Fen_RoundTrips()
    {
        Assert.Equal(XqPosition.StartFen, XqPosition.Start().ToFen());
        const string fen = "3k5/4a4/4b4/9/2p6/9/9/4B4/4A4/4K4 b - - 12 30";
        Assert.Equal(fen, XqPosition.FromFen(fen).ToFen());
    }

    // Published perft counts for the starting position.
    [Theory]
    [InlineData(1, 44)]
    [InlineData(2, 1920)]
    [InlineData(3, 79666)]
    public void Perft_FromStart(int depth, long expected)
    {
        Assert.Equal(expected, XqPosition.Start().Perft(depth));
    }

    [Fact]
    public void Horse_IsBlockedByAPieceOnItsLeg()
    {
        Assert.True(CanMove(XqPosition.StartFen, "b0c2"));
        // A piece on b1 blocks the leg for both forward jumps.
        const string fen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/1P7/RNBAKABNR w - - 0 1";
        Assert.False(CanMove(fen, "b0a2"));
        Assert.False(CanMove(fen, "b0c2"));
    }

    [Fact]
    public void Elephant_CannotCrossTheRiver_OrJumpItsEye()
    {
        const string fen = "3k5/9/9/9/9/9/9/9/9/2B1K4 w - - 0 1";
        // c0 -> e2 ok; but an elephant on e4 cannot go to c6/g6 across the river.
        Assert.True(CanMove(fen, "c0e2"));
        const string onRiver = "3k5/9/9/9/9/4B4/9/9/9/4K4 w - - 0 1";
        Assert.False(CanMove(onRiver, "e4c6"));
        Assert.False(CanMove(onRiver, "e4g6"));
        Assert.True(CanMove(onRiver, "e4c2"));
        const string blockedEye = "3k5/9/9/9/9/9/9/9/3P5/2B1K4 w - - 0 1";
        Assert.False(CanMove(blockedEye, "c0e2"));
    }

    [Fact]
    public void Advisor_AndGeneral_StayInThePalace()
    {
        const string fen = "3k5/9/9/9/9/9/9/9/9/3AK4 w - - 0 1";
        Assert.True(CanMove(fen, "d0e1"));
        Assert.False(CanMove(fen, "d0c1"));
        Assert.True(CanMove(fen, "e0e1"));
        const string edge = "3k5/9/9/9/9/9/9/9/9/5K3 w - - 0 1";
        Assert.False(CanMove(edge, "f0g0"));
    }

    [Fact]
    public void Cannon_CapturesOnlyOverExactlyOneScreen()
    {
        // Red cannon e2, black soldier e6, black chariot e8.
        const string fen = "3k5/4r4/9/4p4/9/9/9/4C4/9/5K3 w - - 0 1";
        Assert.False(CanMove(fen, "e2e6"));   // no screen: cannot capture
        Assert.True(CanMove(fen, "e2e5"));    // moves like a chariot
        Assert.True(CanMove(fen, "e2e8"));    // jumps the soldier to capture the chariot
    }

    [Fact]
    public void Soldier_MovesSidewaysOnlyAfterCrossingTheRiver()
    {
        const string before = "3k5/9/9/9/9/9/4P4/9/9/4K4 w - - 0 1";
        Assert.True(CanMove(before, "e3e4"));
        Assert.False(CanMove(before, "e3d3"));
        const string after = "3k5/9/9/9/4P4/9/9/9/9/4K4 w - - 0 1";
        Assert.True(CanMove(after, "e5d5"));
        Assert.False(CanMove(after, "e5e4"));
    }

    [Fact]
    public void FlyingGeneral_GeneralsMayNotFaceEachOther()
    {
        // Only an advisor between the generals on the e-file: it cannot step aside.
        const string fen = "4k4/9/9/9/9/9/9/9/4A4/4K4 w - - 0 1";
        Assert.False(CanMove(fen, "e1d2"));
        Assert.False(CanMove(fen, "e1f2"));
        Assert.True(XqPosition.FromFen("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1").InCheck(XqSide.Red));
    }

    [Fact]
    public void Checkmate_EndsTheGame()
    {
        // A check the general can escape is not mate.
        var game = new XqGame("3k5/9/9/9/9/9/9/9/9/R3K3R w - - 0 1");
        Assert.True(game.TryMove(M("a0a9")));
        Assert.False(game.IsOver);
        // Chariot d0 holds the d-file, the red general holds the e-file (flying general),
        // and g0-g9 checks along the back rank: no escape.
        var mate = new XqGame("3k5/9/9/9/9/9/9/9/9/3RK1R2 w - - 0 1");
        Assert.True(mate.TryMove(M("g0g9")));
        Assert.True(mate.IsOver);
        Assert.Equal(XqResult.RedWins, mate.Result);
        Assert.Equal(XqEndReason.Checkmate, mate.Reason);
    }

    [Fact]
    public void Stalemate_IsALossForTheSideToMove()
    {
        // Black general d9 is not in check, but e9 faces the red general and d8 is covered by the chariot on rank 8.
        var stuck = new XqGame("3k5/5R3/9/9/9/9/9/9/9/2R1K4 b - - 0 1");
        Assert.True(stuck.IsOver);
        Assert.Equal(XqResult.RedWins, stuck.Result);
        Assert.Equal(XqEndReason.Stalemate, stuck.Reason);
    }

    [Fact]
    public void Draw_WhenNeitherSideCanAttack()
    {
        var game = new XqGame("3ak4/9/9/9/9/9/9/9/4A4/4K4 w - - 0 1");
        Assert.True(game.IsOver);
        Assert.Equal(XqResult.Draw, game.Result);
        Assert.Equal(XqEndReason.NoAttackers, game.Reason);
    }

    [Fact]
    public void Draw_AfterSixtyMovesWithoutACapture()
    {
        var game = new XqGame("3k5/9/9/9/9/9/9/9/9/4K3R w - - 119 80");
        Assert.True(game.TryMove(M("i0i1")));
        Assert.Equal(XqEndReason.NoCaptureLimit, game.Reason);
    }

    [Fact]
    public void Notation_DescribesClassicOpeningMoves()
    {
        var game = new XqGame();
        Assert.True(game.TryMove(M("h2e2")));          // 炮二平五
        Assert.True(game.TryMove(M("h9g7")));          // 馬8進7
        var cannon = game.History[0].Notation;
        Assert.Equal((Pieces.Cannon, 2, '=', 5), (cannon.Piece, cannon.FromFile, cannon.Op, cannon.Dest));
        var horse = game.History[1].Notation;
        Assert.Equal((-Pieces.Horse, 8, '+', 7), (horse.Piece, horse.FromFile, horse.Op, horse.Dest));
        Assert.True(game.TryMove(M("h0g2")));          // 馬二進三
        Assert.Equal((2, '+', 3), (game.History[2].Notation.FromFile, game.History[2].Notation.Op, game.History[2].Notation.Dest));
    }

    [Fact]
    public void Notation_CountsRanksForStraightMoves_AndMarksFrontAndRear()
    {
        // Two red chariots on the a-file: a0 is rear, a3 is front.
        var pos = XqPosition.FromFen("4k4/9/9/9/9/9/R8/9/9/R3K4 w - - 0 1");
        var front = XqNotation.Describe(pos, M("a3a5"));
        Assert.Equal((1, 2, '+', 2), (front.Tandem, front.TandemCount, front.Op, front.Dest));
        var rear = XqNotation.Describe(pos, M("a0a1"));
        Assert.Equal((2, '+', 1), (rear.Tandem, rear.Op, rear.Dest));
    }

    [Fact]
    public void IllegalMoves_AreRejected_AndResignEndsTheGame()
    {
        var game = new XqGame();
        Assert.False(game.TryMove(M("h9g7")));         // Black cannot move first
        Assert.False(game.TryMove(M("a0a5")));         // chariot cannot jump its soldier
        game.Resign(XqSide.Red);
        Assert.Equal(XqResult.BlackWins, game.Result);
        Assert.False(game.TryMove(M("h2e2")));
    }
}

public class XiangqiBotTests
{
    /// <summary>Shorter thinking time so bot-vs-bot games stay quick in CI.</summary>
    static readonly TimeSpan FastBudget = TimeSpan.FromMilliseconds(150);

    [Theory]
    [InlineData(XqLevel.Medium, 1.5)]
    [InlineData(XqLevel.Difficult, 2.8)]
    public void Level_StaysWithinItsTimeBudget_InABusyPosition(XqLevel level, double maxSeconds)
    {
        // A crowded middlegame after a few opening moves.
        var game = new XqGame();
        foreach (var m in new[] { "h2e2", "h9g7", "h0g2", "i9h9", "i0h0", "b9c7" })
            Assert.True(game.TryMove(XqMove.Parse(m)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var move = XqBot.ChooseMove(game.Position, level);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(maxSeconds), $"took {sw.Elapsed}");
        Assert.Contains(move, game.LegalMoves());
    }

    [Fact]
    public void Bot_TakesAFreeChariot()
    {
        // Red cannon can take an undefended black chariot by jumping its own soldier.
        var pos = XqPosition.FromFen("3k5/9/9/9/4r4/9/4P4/4C4/9/5K3 w - - 0 1");
        var move = XqBot.ChooseMove(pos, XqLevel.Medium);
        Assert.Equal(XqMove.Parse("e2e5"), move);
    }

    [Fact]
    public void Bot_FindsMateInOne()
    {
        // Black general d9: chariot f8 covers d8, the red general covers e9 (flying general).
        // A black soldier on i4 can still move, so Black is not stalemated; Ra0-a9 mates.
        var game = new XqGame("3k5/5R3/9/9/9/8p/9/9/9/R3K4 w - - 0 1");
        Assert.False(game.InCheck);
        var move = XqBot.ChooseMove(game.Position, XqLevel.Medium);
        Assert.True(game.TryMove(move));
        Assert.Equal(XqResult.RedWins, game.Result);
    }

    [Theory]
    [InlineData(XqLevel.Beginner, XqLevel.Difficult)]
    [InlineData(XqLevel.Difficult, XqLevel.Beginner)]
    public void BotGames_PlayOnlyLegalMoves_AndFinish(XqLevel red, XqLevel black)
    {
        var rng = new Random(7);
        var game = new XqGame();
        for (int ply = 0; ply < 400 && !game.IsOver; ply++)
        {
            var level = game.SideToMove == XqSide.Red ? red : black;
            var move = XqBot.ChooseMove(game.Position, level, rng, FastBudget);
            Assert.True(game.TryMove(move), $"illegal bot move {move} at ply {ply}");
        }
        Console.WriteLine($"{red} vs {black}: {game.Result} ({game.Reason}) after {game.History.Count} plies");
    }

    /// <summary>Each level should beat the one below it, playing both colours.</summary>
    [Theory]
    [InlineData(XqLevel.Medium, XqLevel.Beginner)]
    [InlineData(XqLevel.Difficult, XqLevel.Medium)]
    public void StrongerLevel_WinsMoreGames(XqLevel stronger, XqLevel weaker)
    {
        int strongWins = 0, weakWins = 0, draws = 0;
        for (int seed = 0; seed < 2; seed++)
        {
            var rng = new Random(seed);
            var game = new XqGame();
            var strongSide = seed % 2 == 0 ? XqSide.Red : XqSide.Black;
            for (int ply = 0; ply < 300 && !game.IsOver; ply++)
            {
                bool strongToMove = game.SideToMove == strongSide;
                var level = strongToMove ? stronger : weaker;
                // Give Difficult a realistic but short think so the test stays quick.
                var budget = level == XqLevel.Difficult ? TimeSpan.FromMilliseconds(400) : FastBudget;
                game.TryMove(XqBot.ChooseMove(game.Position, level, rng, budget));
            }
            var strongResult = strongSide == XqSide.Red ? XqResult.RedWins : XqResult.BlackWins;
            if (game.Result == strongResult) strongWins++;
            else if (game.IsOver && game.Result != XqResult.Draw) weakWins++;
            else draws++;
        }
        Console.WriteLine($"{stronger} {strongWins} - {weakWins} {weaker} ({draws} drawn or unfinished)");
        Assert.True(strongWins > weakWins, $"{stronger} won {strongWins}, {weaker} won {weakWins}");
    }
}
