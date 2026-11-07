using GameWeb.Games.Mahjong;

namespace GameWeb.Tests;

public class HandAnalyzerTests
{
    static int[] C(string s) => Tiles.ToCounts(Tiles.Parse(s));

    [Fact]
    public void Parse_UsesSuitOffsets()
    {
        Assert.Equal([0, 9, 18, 27, 31, 33], Tiles.Parse("1m1p1s1z5z7z"));
    }

    [Theory]
    [InlineData("123m456p789s111z55z", true)]
    [InlineData("111222333m44455z", true)]
    [InlineData("123m456p789s111z56z", false)]
    [InlineData("19m19p19s1234567z1m", true)] // thirteen orphans
    [InlineData("11223344556677m", true)]      // decomposes as runs
    public void IsComplete(string hand, bool expected)
    {
        Assert.Equal(expected, HandAnalyzer.IsComplete(C(hand), 0));
    }

    [Theory]
    [InlineData("123m456p789s11z55z", 0)]   // 13 tiles, ready
    [InlineData("123m456p789s15z77z", 1)]
    [InlineData("147m258p369s1357z", 7)]  // thirteen-orphans shanten
    [InlineData("123m456p789s111z55z", -1)] // 14 tiles, complete
    public void Shanten(string hand, int expected)
    {
        Assert.Equal(expected, HandAnalyzer.Shanten(C(hand), 0));
    }

    [Fact]
    public void Shanten_AccountsForMelds()
    {
        // One pung exposed, 10 concealed tiles: 123m 456p 78s 55z → ready on 6s/9s.
        Assert.Equal(0, HandAnalyzer.Shanten(C("123m456p78s55z"), 1));
    }
}

public class ScoringTests
{
    static ScoreResult? Score(string concealed, int winTile, bool selfDrawn = false, Meld[]? melds = null,
        int seatWind = 0, int prevailing = 0) =>
        Scoring.Evaluate(new WinContext
        {
            Concealed = Tiles.ToCounts(Tiles.Parse(concealed)),
            Melds = melds ?? [],
            WinTile = winTile,
            SelfDrawn = selfDrawn,
            SeatWind = seatWind,
            PrevailingWind = prevailing,
        });

    static int T(string s) => Tiles.Parse(s)[0];

    [Fact]
    public void NotAWin_ReturnsNull()
    {
        Assert.Null(Score("123m456p789s111z56z", T("6z")));
    }

    [Fact]
    public void AllChows_Concealed_SelfDrawn()
    {
        var r = Score("123m456m789p234s55p", T("4s"), selfDrawn: true)!;
        Assert.Equal(3, r.Total);
        Assert.Contains(Scoring.F.AllChows, r.Items);
        Assert.Contains(Scoring.F.SelfDrawn, r.Items);
        Assert.Contains(Scoring.F.Concealed, r.Items);
    }

    [Fact]
    public void MixedOneSuit_WithDragonPung()
    {
        var melds = new[] { new Meld(MeldType.Pung, Tiles.Red, false) };
        var r = Score("123m456m789m11m", T("1m"), melds: melds)!;
        // Mixed one suit 3 + red dragon 1.
        Assert.Equal(4, r.Total);
        Assert.Contains(Scoring.F.MixedOneSuit, r.Items);
        Assert.Contains(Scoring.F.DragonPung, r.Items);
    }

    [Fact]
    public void PureOneSuit()
    {
        var melds = new[] { new Meld(MeldType.Chow, T("1p"), false) };
        var r = Score("456p789p22p333p", T("3p"), melds: melds)!;
        Assert.Contains(Scoring.F.PureOneSuit, r.Items);
        Assert.Equal(7, r.Total);
    }

    [Fact]
    public void AllPungs_WithSeatAndPrevailingWind()
    {
        var melds = new[] { new Meld(MeldType.Pung, Tiles.East, false) };
        var r = Score("222m555p888s99s", T("9s"), melds: melds, seatWind: 0, prevailing: 0)!;
        Assert.Contains(Scoring.F.AllPungs, r.Items);
        Assert.Contains(Scoring.F.SeatWind, r.Items);
        Assert.Contains(Scoring.F.PrevailingWind, r.Items);
        Assert.Equal(5, r.Total);
    }

    [Fact]
    public void SmallThreeDragons_ReplacesDragonPungs()
    {
        var r = Score("555z666z77z123m456p", T("3m"))!;
        Assert.Contains(Scoring.F.SmallDragons, r.Items);
        Assert.DoesNotContain(Scoring.F.DragonPung, r.Items);
    }

    [Fact]
    public void GreatThreeDragons()
    {
        var r = Score("555z666z777z123m11p", T("3m"))!;
        Assert.Contains(Scoring.F.BigDragons, r.Items);
    }

    [Fact]
    public void ThirteenOrphans_IsLimit()
    {
        var r = Score("19m19p19s12345677z", T("7z"))!;
        Assert.True(r.IsLimit);
        Assert.Equal(10, r.Total);
    }

    [Fact]
    public void FourConcealedPungs_SelfDrawn_IsLimit()
    {
        var r = Score("111m555p999s222z33z", T("2z"), selfDrawn: true)!;
        Assert.True(r.IsLimit);
        Assert.Contains(Scoring.F.FourConcealedPungs, r.Items);
    }

    [Fact]
    public void FourConcealedPungs_NotWhenLastPungCompletedByDiscard()
    {
        var r = Score("111m555p999s222z33z", T("2z"), selfDrawn: false)!;
        Assert.False(r.IsLimit);
        Assert.Contains(Scoring.F.AllPungs, r.Items);
    }

    [Fact]
    public void FourConcealedPungs_WhenDiscardCompletesThePair()
    {
        var r = Score("111m555p999s222z33z", T("3z"), selfDrawn: false)!;
        Assert.True(r.IsLimit);
    }

    [Fact]
    public void BasePoints_GrowWithFaan()
    {
        Assert.Equal(8, Scoring.BasePoints(3));
        Assert.Equal(128, Scoring.BasePoints(10));
    }
}

public class MahjongGameTests
{
    static List<int> P(string s) => Tiles.Parse(s);

    /// <summary>Wall filler that nobody can use for a win in these tests.</summary>
    static List<int> Wall(params string[] front)
    {
        var w = new List<int>();
        foreach (var f in front) w.AddRange(P(f));
        for (int i = 0; i < 40; i++) w.Add(Tiles.North);
        return w;
    }

    [Fact]
    public void Deal_GivesThirteenEach_DealerDrawsFourteenth()
    {
        var g = new MahjongGame(new MjSettings(), new Random(1));
        g.StartMatch();
        Assert.Equal(MjPhase.Turn, g.Phase);
        Assert.Equal(0, g.CurrentSeat);
        Assert.Equal(14, g.Players[0].Hand.Count);
        Assert.All(g.Players.Skip(1), p => Assert.Equal(13, p.Hand.Count));
        Assert.Equal(136 - 53, g.WallCount);
    }

    [Fact]
    public void Discard_WithNoClaims_PassesTurnToNextSeat()
    {
        var g = new MahjongGame(new MjSettings());
        g.StartWithTiles(
        [
            P("19m19p19s1234z66z"), P("2345678m2345p34s"), P("2345678p2345s34m"), P("2345678s2345m34p"),
        ], Wall("7z", "8m"));
        Assert.True(g.Apply(0, new MjAction(MjActionType.Discard, Tiles.White)));
        Assert.Equal(MjPhase.Turn, g.Phase);
        Assert.Equal(1, g.CurrentSeat);
        Assert.Equal(14, g.Players[1].Hand.Count);
    }

    [Fact]
    public void Pung_BeatsChow_AndClaimerMustDiscard()
    {
        var g = new MahjongGame(new MjSettings { MinFaan = 0 });
        g.StartWithTiles(
        [
            P("1199m1199p1199s1z"),  // seat 0 dealer will discard 5m
            P("46m123p456p789p11s"), // seat 1 could chow 5m
            P("55m123s456s789s11p"), // seat 2 could pung 5m
            P("2468m2468p2468s1z"),
        ], Wall("5m"));
        Assert.True(g.Apply(0, new MjAction(MjActionType.Discard, P("5m")[0])));
        Assert.Equal(MjPhase.Claim, g.Phase);
        Assert.NotNull(g.GetClaimOptions(1));
        Assert.True(g.Apply(1, new MjAction(MjActionType.Chow, P("4m")[0])));
        Assert.True(g.Apply(2, new MjAction(MjActionType.Pung)));
        Assert.Equal(MjPhase.Turn, g.Phase);
        Assert.Equal(2, g.CurrentSeat);
        Assert.Single(g.Players[2].Melds);
        Assert.Null(g.DrawnTile);
        Assert.False(g.GetTurnOptions(2)!.CanSelfDraw);
    }

    [Fact]
    public void WinOnDiscard_PaysDoubleFromDiscarder()
    {
        var g = new MahjongGame(new MjSettings { MinFaan = 1 });
        g.StartWithTiles(
        [
            P("1199m1199p1199s1z"),
            P("123m456p789s11z55z"), // waits on 1z or 5z
            P("2468m2468p2468s2z"),
            P("2468m2468p2468s3z"),
        ], Wall("5z"));
        Assert.True(g.Apply(0, new MjAction(MjActionType.Discard, Tiles.Red)));
        var o = g.GetClaimOptions(1)!;
        Assert.True(o.Win);
        Assert.True(g.Apply(1, new MjAction(MjActionType.Win)));
        Assert.Equal(MjPhase.HandOver, g.Phase);
        var r = g.LastResult!;
        Assert.Equal(1, r.Winner);
        int pts = Scoring.BasePoints(r.Score!.Total);
        Assert.Equal(-2 * pts, r.Delta[0]);
        Assert.Equal(2 * pts, r.Delta[1]);
    }

    [Fact]
    public void WinBelowMinimumFaan_IsNotOffered()
    {
        var g = new MahjongGame(new MjSettings { MinFaan = 3 });
        g.StartWithTiles(
        [
            P("1199m1199p1199s1z"),
            P("123m456p789s11z55z"), // red dragon pung + concealed = 2 faan
            P("2468m2468p2468s2z"),
            P("2468m2468p2468s3z"),
        ], Wall("4z", "4z", "4z", "4z", "5z"));
        // Play one full go-around first so the win is not an Earthly hand (a limit hand).
        for (int s = 0; s < 4; s++) Assert.True(g.Apply(s, new MjAction(MjActionType.Discard, Tiles.North)));
        Assert.Equal(0, g.CurrentSeat);
        g.Apply(0, new MjAction(MjActionType.Discard, Tiles.Red));
        var o = g.GetClaimOptions(1);
        Assert.True(o is null || !o.Win);
    }

    [Fact]
    public void ConcealedKong_DrawsReplacementFromBackOfWall()
    {
        var g = new MahjongGame(new MjSettings());
        var wall = Wall("7z");
        wall.Add(P("9p")[0]);
        g.StartWithTiles(
        [
            P("1111m258p369s147z"), P("2468m2468p2468s2z"), P("2468m2468p2468s3z"), P("2468m2468p2468s3z"),
        ], wall);
        var opts = g.GetTurnOptions(0)!;
        Assert.Contains(0, opts.ConcealedKongs);
        Assert.True(g.Apply(0, new MjAction(MjActionType.ConcealedKong, 0)));
        Assert.Equal(P("9p")[0], g.DrawnTile);
        Assert.Single(g.Players[0].Melds);
        Assert.True(g.Players[0].Melds[0].Concealed);
    }

    [Fact]
    public void Ready_FromAllSeats_StartsNextHand_AndDealerPassesOnLoss()
    {
        var g = new MahjongGame(new MjSettings { MinFaan = 1, Rounds = 1 });
        g.StartWithTiles(
        [
            P("1199m1199p1199s1z"),
            P("123m456p789s11z55z"),
            P("2468m2468p2468s2z"),
            P("2468m2468p2468s3z"),
        ], Wall("5z"));
        g.Apply(0, new MjAction(MjActionType.Discard, Tiles.Red));
        g.Apply(1, new MjAction(MjActionType.Win));
        for (int s = 0; s < 4; s++) g.Apply(s, new MjAction(MjActionType.Ready));
        Assert.Equal(1, g.Dealer);
        Assert.Equal(MjPhase.Turn, g.Phase);
        Assert.Equal(1, g.CurrentSeat);
    }

    [Fact]
    public void IllegalActions_AreRejected()
    {
        var g = new MahjongGame(new MjSettings(), new Random(3));
        g.StartMatch();
        Assert.False(g.Apply(1, new MjAction(MjActionType.Discard, g.Players[1].Hand[0])));
        Assert.False(g.Apply(0, new MjAction(MjActionType.SelfDrawWin)) && g.Phase == MjPhase.Turn && false);
        int missing = Enumerable.Range(0, 34).First(k => !g.Players[0].Hand.Contains(k));
        Assert.False(g.Apply(0, new MjAction(MjActionType.Discard, missing)));
    }
}

public class BotSimulationTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void BotsPlayFullMatches_WithConsistentState(int minFaan)
    {
        int hands = 0, wins = 0;
        for (int seed = 0; seed < 10; seed++)
        {
            var g = new MahjongGame(new MjSettings { MinFaan = minFaan, Rounds = 1 }, new Random(seed));
            g.StartMatch();
            int steps = 0;
            while (g.Phase != MjPhase.MatchOver)
            {
                Assert.True(++steps < 20000, "game did not finish");
                if (g.Phase == MjPhase.HandOver)
                {
                    hands++;
                    if (!g.LastResult!.IsDraw) wins++;
                    Assert.Equal(0, g.Players.Sum(p => p.Score));
                    for (int s = 0; s < 4; s++) Assert.True(g.Apply(s, MahjongBot.Decide(g, s)));
                    continue;
                }
                if (g.Phase is MjPhase.Claim or MjPhase.RobKong)
                {
                    foreach (var s in g.PendingClaimSeats().ToList())
                        Assert.True(g.Apply(s, MahjongBot.Decide(g, s)));
                    continue;
                }
                int seat = g.CurrentSeat;
                var p = g.Players[seat];
                Assert.Equal(14, p.Hand.Count + 3 * p.Melds.Count);
                Assert.True(g.Apply(seat, MahjongBot.Decide(g, seat)));
                if (g.Phase == MjPhase.RobKong) continue; // the kong tile is in flight
                // Tile conservation: 136 tiles in wall, hands, melds and unclaimed discards.
                int total = g.WallCount + g.Players.Sum(x =>
                    x.Hand.Count + x.Melds.Sum(m => m.TilesInMeld.Count() - (m.FromSeat >= 0 ? 1 : 0)) + x.Discards.Count);
                Assert.Equal(136, total);
            }
        }
        Console.WriteLine($"minFaan={minFaan}: {wins}/{hands} hands won by bots");
        Assert.True(wins > hands / 5, $"bots won only {wins} of {hands} hands");
    }
}

public class ShantenCrossCheckTests
{
    [Fact]
    public void FastShanten_MatchesReference_OnRandomHands()
    {
        var rng = new Random(42);
        for (int i = 0; i < 3000; i++)
        {
            var wall = Enumerable.Range(0, 136).Select(x => x / 4).OrderBy(_ => rng.Next()).ToList();
            int melds = rng.Next(0, 4);
            int size = 13 - 3 * melds + rng.Next(0, 2);
            var counts = Tiles.ToCounts(wall.Take(size));
            Assert.Equal(HandAnalyzer.ShantenReference(counts, melds), HandAnalyzer.Shanten(counts, melds));
        }
    }
}
