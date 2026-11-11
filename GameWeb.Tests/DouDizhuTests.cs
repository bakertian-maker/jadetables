using GameWeb.Games.DouDizhu;

namespace GameWeb.Tests;

public class DouDizhuTests
{
    // Cards by rank label and suit: C("3", 0) is the 3 of spades. Ranks: 3..10, J, Q, K, A, 2.
    static readonly string[] Labels = ["3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A", "2"];
    static int C(string rank, int suit = 0) => Array.IndexOf(Labels, rank) * 4 + suit;
    const int SJ = Cards.SmallJoker, BJ = Cards.BigJoker;

    /// <summary>"3 3 3 4" → four cards, using different suits for repeats.</summary>
    static List<int> H(string ranks)
    {
        var used = new Dictionary<string, int>();
        var list = new List<int>();
        foreach (var r in ranks.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (r == "SJ") { list.Add(SJ); continue; }
            if (r == "BJ") { list.Add(BJ); continue; }
            used[r] = used.GetValueOrDefault(r) + 1;
            list.Add(C(r, used[r] - 1));
        }
        return list;
    }

    static DdzKind? Kind(string ranks) => Combos.Analyze(H(ranks)).FirstOrDefault()?.Kind;

    [Theory]
    [InlineData("3", DdzKind.Single)]
    [InlineData("BJ", DdzKind.Single)]
    [InlineData("7 7", DdzKind.Pair)]
    [InlineData("SJ BJ", DdzKind.Rocket)]
    [InlineData("K K K", DdzKind.Triple)]
    [InlineData("K K K 3", DdzKind.TripleSingle)]
    [InlineData("K K K 3 3", DdzKind.TriplePair)]
    [InlineData("3 4 5 6 7", DdzKind.Straight)]
    [InlineData("10 J Q K A", DdzKind.Straight)]
    [InlineData("3 3 4 4 5 5", DdzKind.PairStraight)]
    [InlineData("3 3 3 4 4 4", DdzKind.Airplane)]
    [InlineData("3 3 3 4 4 4 9 J", DdzKind.AirplaneSingles)]
    [InlineData("3 3 3 4 4 4 9 9 J J", DdzKind.AirplanePairs)]
    [InlineData("8 8 8 8", DdzKind.Bomb)]
    [InlineData("8 8 8 8 3 5", DdzKind.FourTwoSingles)]
    [InlineData("8 8 8 8 3 3 5 5", DdzKind.FourTwoPairs)]
    public void RecognisesEveryKind(string ranks, DdzKind kind) => Assert.Equal(kind, Kind(ranks));

    [Theory]
    [InlineData("3 4")]                 // not a pair
    [InlineData("J Q K A 2")]           // 2 can't be in a straight
    [InlineData("3 4 5 6")]             // too short
    [InlineData("3 3 4 4")]             // only two pairs in a row
    [InlineData("SJ SJ")]               // impossible anyway, but jokers never pair
    [InlineData("K K K SJ BJ")]         // the two jokers aren't a pair
    [InlineData("3 3 3 5 5 5")]         // triples not in a row
    [InlineData("3 3 3 4 4 4 SJ BJ")]   // the rocket can't be wings
    public void RejectsIllegalPlays(string ranks) => Assert.Null(Kind(ranks));

    [Fact]
    public void BiggerAirplaneReading_ComesFirst()
    {
        // 444 555 666 + 3 3 3 reads as a three-triple airplane carrying three singles (top 6)...
        var readings = Combos.Analyze(H("3 3 3 4 4 4 5 5 5 6 6 6"));
        Assert.Equal(DdzKind.Airplane, readings[0].Kind);   // ...but first as a four-triple airplane.
        Assert.Contains(readings, r => r.Kind == DdzKind.AirplaneSingles && r.Rank == 9 - 6);
    }

    [Fact]
    public void BeatingRules()
    {
        DdzCombo P(string s) => Combos.Analyze(H(s))[0];
        Assert.True(P("4").Beats(P("3")));
        Assert.False(P("3").Beats(P("4")));
        Assert.True(P("BJ").Beats(P("SJ")));
        Assert.True(P("2").Beats(P("A")));
        Assert.False(P("4 5 6 7 8 9").Beats(P("3 4 5 6 7")));   // different length
        Assert.True(P("4 5 6 7 8").Beats(P("3 4 5 6 7")));
        Assert.False(P("4 4").Beats(P("3")));                   // different kind
        Assert.True(P("3 3 3 3").Beats(P("2 2")));              // bomb beats anything
        Assert.True(P("4 4 4 4").Beats(P("3 3 3 3")));
        Assert.True(P("SJ BJ").Beats(P("2 2 2 2")));            // rocket beats bombs
        Assert.False(P("2 2 2 2").Beats(P("SJ BJ")));
    }

    [Fact]
    public void Hints_FindTheLowestWinningPlays()
    {
        var hand = H("3 5 5 7 7 7 9 J Q K A 2 2 2 2 SJ BJ");
        var beats = Combos.Beating(hand, Combos.Analyze(H("4 4"))[0]);
        Assert.Equal(DdzKind.Pair, beats[0].Kind);
        Assert.Equal(H("5 5").Select(Cards.Rank), beats[0].Cards.Select(Cards.Rank));
        Assert.Contains(beats, b => b.Kind == DdzKind.Bomb);
        Assert.Equal(DdzKind.Rocket, beats[^1].Kind);
        // Three with one: the kicker is the lone 3, not part of a pair or the bomb.
        var tripleSingle = Combos.OfKind(hand, DdzKind.TripleSingle, 1, -1).First();
        Assert.Equal(new[] { 4, 4, 4, 0 }, tripleSingle.Cards.Select(Cards.Rank).OrderByDescending(r => r).ToArray());
    }

    static DdzGame NewGame(int seed, int hands = 1)
    {
        var g = new DdzGame(new DdzSettings { Hands = hands }, new Random(seed));
        g.StartMatch();
        return g;
    }

    [Fact]
    public void Deal_17Each_ThreeLeft()
    {
        var g = NewGame(1);
        Assert.All(g.Hands, h => Assert.Equal(17, h.Count));
        Assert.Equal(3, g.Bottom.Count);
        Assert.Equal(54, g.Hands.SelectMany(h => h).Concat(g.Bottom).Distinct().Count());
        Assert.Equal(DdzPhase.Bidding, g.Phase);
    }

    [Fact]
    public void Bidding_HighestBidderBecomesLandlord_AndTakesTheBottomCards()
    {
        var g = NewGame(2);
        int first = g.CurrentSeat;
        Assert.True(g.Apply(first, new DdzAction(DdzActionType.Bid, 1)));
        Assert.False(g.Apply((first + 1) % 3, new DdzAction(DdzActionType.Bid, 1)));   // must bid higher
        Assert.True(g.Apply((first + 1) % 3, new DdzAction(DdzActionType.Bid, 2)));
        Assert.True(g.Apply((first + 2) % 3, new DdzAction(DdzActionType.Pass)));
        Assert.Equal(DdzPhase.Playing, g.Phase);
        int landlord = (first + 1) % 3;
        Assert.Equal(landlord, g.Landlord);
        Assert.Equal(2, g.Bid);
        Assert.Equal(20, g.Hands[landlord].Count);
        Assert.Equal(landlord, g.CurrentSeat);
        Assert.True(g.IsLeading(landlord));
    }

    [Fact]
    public void BiddingThree_EndsTheBiddingAtOnce()
    {
        var g = NewGame(3);
        int first = g.CurrentSeat;
        Assert.True(g.Apply(first, new DdzAction(DdzActionType.Bid, 3)));
        Assert.Equal(first, g.Landlord);
        Assert.Equal(DdzPhase.Playing, g.Phase);
    }

    [Fact]
    public void EveryonePassing_DealsAgain()
    {
        var g = NewGame(4);
        int first = g.CurrentSeat;
        var before = g.Hands[0].ToList();
        for (int i = 0; i < 3; i++) Assert.True(g.Apply((first + i) % 3, new DdzAction(DdzActionType.Pass)));
        Assert.Equal(DdzPhase.Bidding, g.Phase);
        Assert.Equal((first + 1) % 3, g.CurrentSeat);
        Assert.NotEqual(before, g.Hands[0]);
    }

    [Fact]
    public void TwoPasses_GiveTheLeadBack()
    {
        var g = NewGame(5);
        g.Apply(g.CurrentSeat, new DdzAction(DdzActionType.Bid, 3));
        int landlord = g.Landlord;
        Assert.False(g.Apply(landlord, new DdzAction(DdzActionType.Pass)));   // the leader must play
        var low = g.Hands[landlord].OrderBy(Cards.Rank).First();
        Assert.True(g.Apply(landlord, new DdzAction(DdzActionType.Play, Cards: [low])));
        Assert.True(g.Apply((landlord + 1) % 3, new DdzAction(DdzActionType.Pass)));
        Assert.True(g.Apply((landlord + 2) % 3, new DdzAction(DdzActionType.Pass)));
        Assert.True(g.IsLeading(landlord));
    }

    [Fact]
    public void IllegalPlays_AreRefused()
    {
        var g = NewGame(6);
        g.Apply(g.CurrentSeat, new DdzAction(DdzActionType.Bid, 3));
        int landlord = g.Landlord, next = (landlord + 1) % 3;
        var notMine = g.Hands[next][0];
        Assert.False(g.Apply(landlord, new DdzAction(DdzActionType.Play, Cards: [notMine])));
        Assert.False(g.Apply(next, new DdzAction(DdzActionType.Play, Cards: [notMine])));   // not their turn
    }

    [Fact]
    public void Scoring_LandlordWinsDouble_BombsAndSpringDouble()
    {
        // Rig a hand: the landlord holds a bomb and goes out at once (a spring).
        var g = NewGame(7);
        g.Apply(g.CurrentSeat, new DdzAction(DdzActionType.Bid, 2));
        for (int i = 0; i < 2; i++) g.Apply(g.CurrentSeat, new DdzAction(DdzActionType.Pass));
        int landlord = g.Landlord;
        g.Hands[landlord].Clear();
        g.Hands[landlord].AddRange(H("9 9 9 9"));
        Assert.True(g.Apply(landlord, new DdzAction(DdzActionType.Play, Cards: g.Hands[landlord].ToList())));
        var r = g.LastResult!;
        Assert.True(r.LandlordWon);
        Assert.True(r.Spring);
        Assert.Equal(4, r.Multiplier);            // bomb ×2, spring ×2
        Assert.Equal(8, r.Points);                // bid 2 × 4
        Assert.Equal(16, r.Changes[landlord]);
        Assert.Equal(0, r.Changes.Sum());
        Assert.Equal(DdzPhase.HandOver, g.Phase);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void ComputerPlayers_FinishWholeMatches_WithOnlyLegalMoves(int seed)
    {
        var g = NewGame(seed, hands: 3);
        for (int step = 0; step < 2000 && g.Phase != DdzPhase.MatchOver; step++)
        {
            int seat = g.Phase == DdzPhase.HandOver ? Array.FindIndex(g.Ready, r => !r) : g.CurrentSeat;
            var action = DdzBot.Decide(g, seat);
            Assert.True(g.Apply(seat, action), $"illegal {action.Type} by seat {seat} in {g.Phase}");
        }
        Assert.Equal(DdzPhase.MatchOver, g.Phase);
        Assert.Equal(0, g.Scores.Sum());          // points only move between players
    }

    [Fact]
    public void TimeoutAction_IsAlwaysLegal()
    {
        var g = NewGame(21);
        for (int step = 0; step < 300 && g.Phase is DdzPhase.Bidding or DdzPhase.Playing; step++)
            Assert.True(g.Apply(g.CurrentSeat, g.AutoAction(g.CurrentSeat)));
    }
}

public class DouDizhuRoomTests
{
    [Fact]
    public void QuickPlay_StartsWithTwoComputerPlayers_AndHidesTheirCards()
    {
        using var room = new DdzRoom("DDZ01", new DdzRoomSettings { QuickPlay = true, TurnSeconds = 0 });
        room.Connect("me", "Me");
        var view = room.GetView("me");
        Assert.True(view.Started);
        Assert.Equal(DdzPhase.Bidding, view.Phase);
        Assert.Equal(17, view.Me!.Hand!.Count);
        Assert.All(view.Seats.Where(s => s.Seat != view.MySeat), s =>
        {
            Assert.True(s.IsBot);
            Assert.Null(s.Hand);          // never sent to other players
            Assert.Equal(17, s.CardCount);
        });
        Assert.Null(view.Bottom);         // the landlord's cards stay hidden while bidding
    }

    [Fact]
    public void Spectators_SeeNoHands()
    {
        using var room = new DdzRoom("DDZ02", new DdzRoomSettings { QuickPlay = true });
        room.Connect("me", "Me");
        var watcher = room.GetView("someone-else");
        Assert.Equal(-1, watcher.MySeat);
        Assert.All(watcher.Seats, s => Assert.Null(s.Hand));
        Assert.Empty(watcher.AllowedBids);
    }
}

public class DouDizhuCounterTests
{
    [Fact]
    public void PlayedCards_AreTracked_AndClearedOnTheNextDeal()
    {
        var g = new DdzGame(new DdzSettings { Hands = 2 }, new Random(31));
        g.StartMatch();
        g.Apply(g.CurrentSeat, new DdzAction(DdzActionType.Bid, 3));
        int landlord = g.Landlord;
        var low = g.Hands[landlord].OrderBy(Cards.Rank).First();
        g.Apply(landlord, new DdzAction(DdzActionType.Play, Cards: [low]));
        Assert.Equal([low], g.Played);

        // Finish the hand quickly: the landlord's remaining cards as one rigged play.
        g.Hands[landlord].Clear();
        g.Hands[landlord].Add(Cards.BigJoker == low ? Cards.SmallJoker : Cards.BigJoker);
        g.Apply((landlord + 1) % 3, new DdzAction(DdzActionType.Pass));
        g.Apply((landlord + 2) % 3, new DdzAction(DdzActionType.Pass));
        g.Apply(landlord, new DdzAction(DdzActionType.Play, Cards: g.Hands[landlord].ToList()));
        Assert.Equal(DdzPhase.HandOver, g.Phase);
        for (int s = 0; s < 3; s++) g.Apply(s, new DdzAction(DdzActionType.Ready));
        Assert.Empty(g.Played);
    }
}
