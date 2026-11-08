namespace GameWeb.Games.Xiangqi;

/// <summary>A quiet position from a finished game: its active weights and the game's result for Red (1, 0.5, 0).</summary>
public sealed record TuningSample(int[] Index, sbyte[] Value, float Result, int GameId);

public sealed record TuningReport(
    double K, int TrainSamples, int TestSamples,
    double TrainErrorBefore, double TrainErrorAfter, double TestErrorBefore, double TestErrorAfter,
    int[] Weights);

/// <summary>
/// Texel tuning: adjusts the evaluation weights so that sigmoid(K * score / 400) predicts the game
/// results of quiet positions as well as possible (mean squared error). The score is linear in the
/// weights, so the gradient is exact and cheap. K is fitted first with the starting weights and then
/// kept fixed, which anchors the scale (a soldier stays about 100 points). An L2 pull towards the
/// starting weights keeps rarely seen squares from wandering on noise. Data is split by game, so the
/// test error measures games the tuner never saw.
/// </summary>
public static class XqTuner
{
    /// <summary>Opening positions are skipped: they're in the book and say little about the result.</summary>
    public const int SkipPlies = 10;

    public static List<TuningSample> Samples(IEnumerable<(int GameId, IReadOnlyList<XqMove> Moves, XqResult Result)> games, XqEval eval)
    {
        var samples = new List<TuningSample>();
        var features = new Dictionary<int, int>();
        foreach (var (gameId, moves, result) in games)
        {
            float target = result switch { XqResult.RedWins => 1f, XqResult.BlackWins => 0f, _ => 0.5f };
            var pos = XqPosition.Start();
            for (int i = 0; i < moves.Count; i++)
            {
                if (i >= SkipPlies && XqBot.IsQuiet(pos, eval))
                {
                    XqEval.Features(pos, features);
                    samples.Add(new TuningSample(
                        features.Keys.ToArray(), features.Values.Select(v => (sbyte)v).ToArray(), target, gameId));
                }
                pos.Make(moves[i]);
            }
        }
        return samples;
    }

    static double Predict(TuningSample s, ReadOnlySpan<double> w, double k)
    {
        double score = 0;
        for (int j = 0; j < s.Index.Length; j++) score += w[s.Index[j]] * s.Value[j];
        return 1.0 / (1.0 + Math.Pow(10, -k * score / 400.0));
    }

    public static double Error(IReadOnlyList<TuningSample> samples, double[] w, double k)
    {
        if (samples.Count == 0) return 0;
        double sum = 0;
        foreach (var s in samples)
        {
            double d = s.Result - Predict(s, w, k);
            sum += d * d;
        }
        return sum / samples.Count;
    }

    /// <summary>The K (sigmoid steepness) that best fits the starting weights.</summary>
    public static double FindK(IReadOnlyList<TuningSample> samples, double[] w)
    {
        double best = 1, bestError = double.MaxValue;
        for (double k = 0.05; k <= 3.0; k += 0.05)
        {
            double e = Error(samples, w, k);
            if (e < bestError) { bestError = e; best = k; }
        }
        // Refine around the best coarse value.
        for (double k = Math.Max(0.01, best - 0.05); k <= best + 0.05; k += 0.005)
        {
            double e = Error(samples, w, k);
            if (e < bestError) { bestError = e; best = k; }
        }
        return best;
    }

    /// <param name="frozen">Weights that never change (e.g. the general's value).</param>
    public static TuningReport Tune(IReadOnlyList<TuningSample> all, int[] start, int iterations = 600,
        double learningRate = 2.0, double l2 = 5e-9, ISet<int>? frozen = null, TextWriter? progress = null)
    {
        // Hold out about 10% of games for testing.
        var test = all.Where(s => Hash(s.GameId) % 10 == 0).ToList();
        var train = all.Where(s => Hash(s.GameId) % 10 != 0).ToList();
        var w0 = start.Select(x => (double)x).ToArray();
        var w = (double[])w0.Clone();
        double k = FindK(train, w);
        double trainBefore = Error(train, w, k), testBefore = Error(test, w, k);

        // Adam optimiser.
        int n = w.Length;
        var m = new double[n];
        var v = new double[n];
        var grad = new double[n];
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        double scale = Math.Log(10) * k / 400.0;
        // Early stopping: keep the weights with the lowest test error seen so far.
        var best = (double[])w.Clone();
        double bestTest = testBefore;
        for (int it = 1; it <= iterations; it++)
        {
            Array.Clear(grad);
            foreach (var s in train)
            {
                double p = Predict(s, w, k);
                double g = -2.0 * (s.Result - p) * p * (1 - p) * scale;
                for (int j = 0; j < s.Index.Length; j++) grad[s.Index[j]] += g * s.Value[j];
            }
            for (int i = 0; i < n; i++)
            {
                if (frozen?.Contains(i) == true) continue;
                double gi = grad[i] / train.Count + 2 * l2 * (w[i] - w0[i]);
                m[i] = beta1 * m[i] + (1 - beta1) * gi;
                v[i] = beta2 * v[i] + (1 - beta2) * gi * gi;
                double mHat = m[i] / (1 - Math.Pow(beta1, it));
                double vHat = v[i] / (1 - Math.Pow(beta2, it));
                w[i] -= learningRate * mHat / (Math.Sqrt(vHat) + eps);
            }
            if (it % 25 == 0 || it == iterations)
            {
                double testError = test.Count > 0 ? Error(test, w, k) : Error(train, w, k);
                if (testError < bestTest) { bestTest = testError; best = (double[])w.Clone(); }
                if (it % 100 == 0 || it == iterations)
                    progress?.WriteLine($"  iteration {it}: train error {Error(train, w, k):0.000000}, test error {testError:0.000000}");
            }
        }

        var rounded = best.Select(x => (int)Math.Round(x)).ToArray();
        var wr = rounded.Select(x => (double)x).ToArray();
        return new TuningReport(k, train.Count, test.Count, trainBefore, Error(train, wr, k), testBefore, Error(test, wr, k), rounded);
    }

    static uint Hash(int gameId) => unchecked((uint)gameId * 2654435761u) >> 8;
}
