using GameWeb.Games.Xiangqi;

namespace GameWeb.Tests;

public class TunerTests
{
    static List<int> CannonSquares = [];

    /// <summary>Random but legal positions from random play, with Red to move or Black to move.</summary>
    static List<XqPosition> RandomPositions(int count, int seed)
    {
        var rng = new Random(seed);
        var list = new List<XqPosition>();
        while (list.Count < count)
        {
            var game = new XqGame();
            int plies = rng.Next(12, 60);
            for (int i = 0; i < plies && !game.IsOver; i++)
            {
                var moves = game.LegalMoves();
                game.TryMove(moves[rng.Next(moves.Count)]);
            }
            if (!game.IsOver) list.Add(game.Position.Clone());
        }
        return list;
    }

    [Fact]
    public void Tuner_RecoversAPlantedChange()
    {
        // "True" evaluation: the cannon is worth 150 points more than the hand-written one says.
        var trueWeights = XqEval.Hand.Weights.ToArray();
        trueWeights[Pieces.Cannon] += 150;
        var truth = new XqEval(trueWeights);

        var rng = new Random(11);
        var features = new Dictionary<int, int>();
        var samples = new List<TuningSample>();
        int id = 0;
        var cannonSquares = new HashSet<int>();
        foreach (var pos in RandomPositions(20000, 3))
        {
            for (int sq = 0; sq < 90; sq++)
                if (Pieces.Type(pos[sq]) == Pieces.Cannon) cannonSquares.Add(XqEval.PstIndex(pos[sq], sq) - XqEval.PstOffset(Pieces.Cannon));
            double p = 1.0 / (1.0 + Math.Pow(10, -truth.ScoreForRed(pos) / 400.0));
            float result = rng.NextDouble() < p ? 1f : 0f;     // results drawn from the true evaluation
            XqEval.Features(pos, features);
            samples.Add(new TuningSample(features.Keys.ToArray(), features.Values.Select(v => (sbyte)v).ToArray(), result, id++));
        }

        CannonSquares = cannonSquares.ToList();
        var report = XqTuner.Tune(samples, XqEval.Hand.Weights.ToArray(), iterations: 400, learningRate: 3, frozen: new HashSet<int> { 0, Pieces.General });
        // A cannon's worth is its value plus its square bonus; the tuner may move either, since every cannon
        // switches on both. Compare the average over the squares cannons actually stood on.
        double Effective(IReadOnlyList<int> w) => w[Pieces.Cannon] + CannonSquares.Average(i => w[XqEval.PstOffset(Pieces.Cannon) + i]);
        double before = Effective(XqEval.Hand.Weights), after = Effective(report.Weights);
        Console.WriteLine($"Cannon effective value {before:0} -> {after:0} (true {before + 150:0}); " +
                          $"test error {report.TestErrorBefore:0.0000} -> {report.TestErrorAfter:0.0000}, K {report.K:0.00}");
        Assert.True(after > before + 75, $"cannon only moved to {after:0}");
        Assert.True(report.TestErrorAfter < report.TestErrorBefore);
    }

    [Fact]
    public void Samples_SkipTheOpening_AndNoisyPositions()
    {
        var game = new XqGame();
        var rng = new Random(1);
        for (int i = 0; i < 40 && !game.IsOver; i++)
        {
            var moves = game.LegalMoves();
            game.TryMove(moves[rng.Next(moves.Count)]);
        }
        var samples = XqTuner.Samples([(1, game.History.Select(h => h.Move).ToList(), XqResult.Draw)], XqEval.Hand);
        Assert.InRange(samples.Count, 1, game.History.Count - XqTuner.SkipPlies);
        Assert.All(samples, s => Assert.Equal(0.5f, s.Result));
    }
}
