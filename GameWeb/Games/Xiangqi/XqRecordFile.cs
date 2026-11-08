using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GameWeb.Games.Xiangqi;

/// <summary>
/// One game record in the Chinese text format, e.g.:
/// <code>
/// 棋局结果红先胜
/// 步数57
/// 开局中炮对进左马
/// * ==== 棋局开始 ====
/// * 1.炮二平五马8进7
/// ...
/// </code>
/// The moves are replayed through the rules engine; the stated result is checked against the board.
/// </summary>
public sealed partial class XqRecordFile
{
    [GeneratedRegex(@"^\s*(棋局结果|结果|步数|开局|类型|轮次|赛事|红方|黑方|日期)\s*[:：]?\s*(.*?)\s*$")]
    private static partial Regex MetadataLine();

    public string? OpeningName { get; private init; }
    public int? StatedPlies { get; private init; }
    public string? StatedResultText { get; private init; }
    public XqResult? StatedResult { get; private init; }
    public XqNotationParser.ParsedGame Parsed { get; private init; } = default!;
    /// <summary>The result to record: the stated one if it agrees with the board, or the board's own (checkmate, stalemate).</summary>
    public XqResult Result { get; private init; }
    /// <summary>Why the game can't be imported, or null if it can.</summary>
    public string? Problem { get; private init; }
    public IReadOnlyList<XqMove> Moves => Parsed.Moves;
    /// <summary>Coordinate notation, as stored in the database: "h2e2 h9g7 …".</summary>
    public string MovesText => string.Join(' ', Moves.Select(m => m.ToString()));

    /// <summary>Identifies the game itself (moves and result), so the same game in two files is imported once.</summary>
    public byte[] ContentHash() => SHA256.HashData(Encoding.ASCII.GetBytes($"{(int)Result}|{MovesText}"));

    public static XqRecordFile Parse(string text)
    {
        string? opening = null, resultText = null;
        int? plies = null;
        var moveLines = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var m = MetadataLine().Match(line);
            if (!m.Success)
            {
                moveLines.AppendLine(line);
                continue;
            }
            string value = m.Groups[2].Value;
            switch (m.Groups[1].Value)
            {
                case "棋局结果" or "结果": resultText = value; break;
                case "步数": if (int.TryParse(value, out var n)) plies = n; break;
                case "开局": opening = value.Length > 0 ? value : null; break;
            }
        }

        var parsed = XqNotationParser.ParseGame(moveLines.ToString());
        var stated = ParseResult(resultText);
        var game = parsed.Game;
        var board = game.IsOver ? game.Result : (XqResult?)null;

        string? problem = null;
        if (parsed.Error is not null) problem = parsed.Error;
        else if (parsed.Moves.Count == 0) problem = "no moves found";
        else if (plies is int p && p != parsed.Moves.Count) problem = $"步数 says {p} moves, but the record has {parsed.Moves.Count}";
        else if (stated is not null && board is not null && stated != board)
            problem = $"棋局结果 says {stated}, but the final position is {board} ({game.Reason})";
        else if (resultText is not null && stated is null) problem = $"unknown result \"{resultText}\"";
        else if (stated is null && board is null) problem = "no result: the record doesn't state one and the game doesn't end on the board";

        return new XqRecordFile
        {
            OpeningName = opening,
            StatedPlies = plies,
            StatedResultText = resultText,
            StatedResult = stated,
            Parsed = parsed,
            Result = stated ?? board ?? XqResult.Ongoing,
            Problem = problem,
        };
    }

    /// <summary>"红先胜" / "红胜" → Red wins; "黑胜" / "红先负" → Black wins; "和" / "红先和" → draw.</summary>
    public static XqResult? ParseResult(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace("先", "").Replace(" ", "").Replace("　", "");
        if (t.Contains('和')) return XqResult.Draw;
        if (t is "红胜" or "紅勝" or "黑负" or "黑負") return XqResult.RedWins;
        if (t is "黑胜" or "黑勝" or "红负" or "紅負") return XqResult.BlackWins;
        return null;
    }

    /// <summary>
    /// Reads a game file in UTF-8 (with or without BOM), UTF-16 with BOM, or GB18030/GBK, the usual
    /// encoding of Chinese game files.
    /// </summary>
    public static string ReadText(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }
}
