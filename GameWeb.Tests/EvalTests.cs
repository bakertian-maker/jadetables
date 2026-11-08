using GameWeb.Games.Xiangqi;

namespace GameWeb.Tests;

public class EvalTests
{
    /// <summary>The original formula, kept here to prove the table-driven evaluation reproduces it exactly.</summary>
    static int OriginalPieceScore(int piece, int sq)
    {
        int[] values = [0, 0, 200, 200, 400, 900, 450, 100];
        var side = Pieces.SideOf(piece);
        int file = Sq.File(sq);
        int rank = side == XqSide.Red ? Sq.Rank(sq) : 9 - Sq.Rank(sq);
        int centre = Math.Abs(file - 4);
        int type = Pieces.Type(piece);
        int v = values[type];
        switch (type)
        {
            case Pieces.Soldier:
                if (rank >= 5) v += 90 + (rank == 9 ? -40 : (rank - 5) * 12) + (centre <= 1 ? 20 : 0);
                else if (rank == 4) v += 10;
                break;
            case Pieces.Horse: v += -centre * 6 + (rank is >= 2 and <= 7 ? 15 : 0) + (rank >= 5 ? 10 : 0); break;
            case Pieces.Chariot: v += (rank >= 3 ? 10 : 0) + (centre is 1 ? 6 : 0); break;
            case Pieces.Cannon: v += (file == 4 ? 15 : 0) + (rank == 2 ? 5 : 0); break;
            case Pieces.General: v += file == 4 ? 0 : -10; break;
        }
        return v;
    }

    [Fact]
    public void HandWeights_ReproduceTheOriginalFormula_OnEveryPieceAndSquare()
    {
        for (int piece = -7; piece <= 7; piece++)
        {
            if (piece == 0) continue;
            for (int sq = 0; sq < 90; sq++)
            {
                int expected = OriginalPieceScore(piece, sq) * (piece > 0 ? 1 : -1);
                // One-piece "position" evaluated through the table.
                var fenBoard = OnePiece(piece, sq);
                Assert.Equal(expected, XqEval.Hand.ScoreForRed(XqPosition.FromFen(fenBoard)));
            }
        }
    }

    static string OnePiece(int piece, int sq)
    {
        var ranks = new List<string>();
        for (int rank = 9; rank >= 0; rank--)
        {
            if (rank != Sq.Rank(sq)) { ranks.Add("9"); continue; }
            int f = Sq.File(sq);
            ranks.Add((f > 0 ? f.ToString() : "") + Pieces.FenChar(piece) + (f < 8 ? (8 - f).ToString() : ""));
        }
        return string.Join('/', ranks) + " w - - 0 1";
    }

    [Fact]
    public void Features_TimesWeights_EqualsTheScore()
    {
        var game = new XqGame();
        var rng = new Random(4);
        var features = new Dictionary<int, int>();
        for (int i = 0; i < 40 && !game.IsOver; i++)
        {
            var moves = game.LegalMoves();
            game.TryMove(moves[rng.Next(moves.Count)]);
            XqEval.Features(game.Position, features);
            // The general's value is fixed, so it isn't a feature; it cancels out anyway.
            int fromFeatures = features.Sum(f => XqEval.Hand.Weights[f.Key] * f.Value);
            Assert.Equal(XqEval.Hand.ScoreForRed(game.Position), fromFeatures);
        }
    }

    [Fact]
    public void Evaluation_IsSymmetric_BetweenRedAndBlack()
    {
        // The start position is a mirror image: the score must be zero.
        Assert.Equal(0, XqEval.Hand.ScoreForRed(XqPosition.Start()));
    }
}
