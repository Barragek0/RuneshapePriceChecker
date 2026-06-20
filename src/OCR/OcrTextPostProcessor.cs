using System.Text.RegularExpressions;
using StructLinq;

namespace RuneshapePriceChecker.OCR;

internal static class OcrTextPostProcessor
{
    private static readonly Regex MultiWhitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex NonNameChars = new("[^\\p{L}\\p{N} '()\\-]+", RegexOptions.Compiled);

    public static IReadOnlyList<string> ExtractLikelyItemNames(string rawText)
    {
        return rawText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .ToStructEnumerable()
            .Select(NormalizeOcrLine)
            .Where(line => line.Length >= 3 && ContainsLetter(line))
            .ToArray();
    }

    private static bool ContainsLetter(string s)
    {
        foreach (var c in s)
            if (char.IsLetter(c)) return true;
        return false;
    }

    public static (string[] Names, int[] MatchedYPositions) ExtractWithYPositions(
        string rawText, int[] rowYPositions, string? language = null)
    {
        var raw = rawText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .ToStructEnumerable()
            .Select(NormalizeOcrLine)
            .Where(line => line.Length >= 3 && ContainsLetter(line))
            .ToArray();

        var count = Math.Min(raw.Length, rowYPositions.Length);
        var names = new string[count];
        var yPositions = new int[count];
        Array.Copy(raw, names, count);
        Array.Copy(rowYPositions, yPositions, count);
        return (names, yPositions);
    }

    public static IReadOnlyList<string> ExtractLikelyItemNames(string rawText, string? language = null)
    {
        var raw = rawText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .ToStructEnumerable()
            .Select(NormalizeOcrLine)
            .Where(line => line.Length >= 3 && ContainsLetter(line))
            .ToArray();

        return raw;
    }

    private static string NormalizeOcrLine(string line)
    {
        var normalized = line.Replace("�", "'").Replace('`', '\'');
        normalized = NonNameChars.Replace(normalized, " ");
        normalized = MultiWhitespace.Replace(normalized, " ").Trim();

        return normalized.Trim(' ', '-', '\'', ',');
    }
}
