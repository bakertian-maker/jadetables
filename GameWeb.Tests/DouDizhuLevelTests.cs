using GameWeb.Games.DouDizhu;
using Xunit.Abstractions;

namespace GameWeb.Tests;

public class DouDizhuLevelTests(ITestOutputHelper output)
{
    /// <summary>
    /// Plays <paramref name="deals"/> single hands with one <paramref name="strong"/> player and two
    /// <paramref name="weak"/> ones, moving the strong player round the table each deal. Returns the
    /// strong player's average points per deal (points only move between players, so above 0 means it
    /// takes points off the others).
    /// </summary>
    static double StrongPlayerAverage(DdzLevel strong, DdzLevel weak, int deals, int seed)
    {
        var rng = new Random(seed);
        long total = 0;
        for (int d = 0; d < deals; d++)
        {
            int strongSeat = d % DdzGame.Players;
            var g = new DdzGame(new DdzSettings { Hands = 1 }, new Random(rng.Next()));
            g.StartMatch();
            for (int step = 0; step < 500 && g.Phase is DdzPhase.Bidding or DdzPhase.Playing; step++)
            {
                int seat = g.CurrentSeat;
                var action = DdzBot.Decide(g, seat, seat == strongSeat ? strong : weak, rng);
                string before = $"hand {Show(g.Hands[seat])} lead {(g.LastCombo is null ? "-" : g.LastCombo.Kind + " " + Show(g.LastCombo.Cards))} last {g.LastSeat} seat {seat} landlord {g.Landlord}";
                Assert.True(g.Apply(seat, action), $"illegal {action.Type} {Show(action.Cards ?? [])} by a {(seat == strongSeat ? strong : weak)} player; {before}");
            }
            total += g.Scores[strongSeat];
        }
        return (double)total / deals;
    }

    static string Show(IEnumerable<int> cards) => string.Join(" ", cards.Select(c => Cards.IsJoker(c) ? (c == Cards.BigJoker ? "BJ" : "SJ") : Cards.RankLabel(c)));

    [Theory]
    [InlineData(DdzLevel.Beginner)]
    [InlineData(DdzLevel.Medium)]
    [InlineData(DdzLevel.Difficult)]
    public void EveryLevel_PlaysOnlyLegalMoves(DdzLevel level)
    {
        for (int seed = 1; seed <= 3; seed++)
        {
            var g = new DdzGame(new DdzSettings { Hands = 3 }, new Random(seed));
            g.StartMatch();
            var rng = new Random(seed);
            for (int step = 0; step < 2000 && g.Phase != DdzPhase.MatchOver; step++)
            {
                int seat = g.Phase == DdzPhase.HandOver ? Array.FindIndex(g.Ready, r => !r) : g.CurrentSeat;
                Assert.True(g.Apply(seat, DdzBot.Decide(g, seat, level, rng)));
            }
            Assert.Equal(DdzPhase.MatchOver, g.Phase);
        }
    }

    [Fact]
    public void LookAhead_ImaginesOnlyWhatThePlayerCouldKnow()
    {
        var g = new DdzGame(new DdzSettings(), new Random(5));
        g.StartMatch();
        while (g.Phase == DdzPhase.Bidding) g.Apply(g.CurrentSeat, new DdzAction(DdzActionType.Bid, 3));
        for (int i = 0; i < 4; i++) g.Apply(g.CurrentSeat, DdzBot.Decide(g, g.CurrentSeat));   // a few plays in
        int viewer = (g.Landlord + 1) % DdzGame.Players;
        var bottomLeft = g.Bottom.Where(c => g.Hands[g.Landlord].Contains(c)).ToList();

        var seenTwice = new HashSet<string>();
        for (int k = 0; k < 5; k++)
        {
            var sim = g.Imagine(viewer, new Random(k));
            Assert.Equal(g.Hands[viewer], sim.Hands[viewer]);                               // own cards as they are
            for (int s = 0; s < DdzGame.Players; s++) Assert.Equal(g.Hands[s].Count, sim.Hands[s].Count);
            Assert.All(bottomLeft, c => Assert.Contains(c, sim.Hands[g.Landlord]));          // face-up bottom cards stay put
            Assert.Equal(Cards.Count, sim.Hands.Sum(h => h.Count) + sim.Played.Count);        // every card once
            Assert.Equal(Cards.Count, sim.Hands.SelectMany(h => h).Concat(sim.Played).Distinct().Count());
            seenTwice.Add(string.Join(",", sim.Hands[g.Landlord].Order()));
            // The real game is untouched.
            Assert.Equal(DdzPhase.Playing, g.Phase);
        }
        Assert.True(seenTwice.Count > 1, "the hidden cards should be dealt differently each time");
    }

    [Fact]
    public void Medium_TakesPointsOffBeginners()
    {
        double average = StrongPlayerAverage(DdzLevel.Medium, DdzLevel.Beginner, deals: 1500, seed: 7);
        output.WriteLine($"Medium vs two Beginners: {average:+0.00;-0.00} points per deal");
        Assert.True(average > 1, $"Medium averaged {average:0.00} points a deal against Beginners");
    }

    /// <summary>Slow (Difficult plays every option out on random deals), so release builds skip it.</summary>
    [Fact]
    [Trait("Category", "Strength")]
    public void Difficult_TakesPointsOffMediumPlayers()
    {
        double average = StrongPlayerAverage(DdzLevel.Difficult, DdzLevel.Medium, deals: 300, seed: 7);
        output.WriteLine($"Difficult vs two Medium: {average:+0.00;-0.00} points per deal");
        Assert.True(average > 0.5, $"Difficult averaged {average:0.00} points a deal against Medium players");
    }
}
