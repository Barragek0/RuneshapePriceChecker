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
        var lines = rawText.Split(['\r', '\n'], StringSplitOptions.None);
        var names = new List<string>();
        var yPositions = new List<int>();
        for (var i = 0; i < Math.Min(lines.Length, rowYPositions.Length); i++)
        {
            var trimmed = NormalizeOcrLine(lines[i]);
            if (trimmed.Length >= 3 && ContainsLetter(trimmed))
            {
                names.Add(trimmed);
                yPositions.Add(rowYPositions[i]);
            }
        }
        return (names.ToArray(), yPositions.ToArray());
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
