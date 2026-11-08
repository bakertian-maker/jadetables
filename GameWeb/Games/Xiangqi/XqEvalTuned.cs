namespace GameWeb.Games.Xiangqi;

/// <summary>
/// Tuned evaluation weights, written by the tune-xiangqi command. Null means "not tuned yet":
/// the computer then plays with the hand-written evaluation (<see cref="XqEval.Hand"/>).
/// </summary>
public static class XqEvalTuned
{
    public static readonly int[]? Weights = null;
}
