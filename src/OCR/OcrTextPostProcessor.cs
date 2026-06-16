using System.Text.RegularExpressions;
using RuneshapePriceChecker.Pricing;
using StructLinq;

namespace RuneshapePriceChecker.OCR;

internal static class OcrTextPostProcessor
{
    private static readonly Regex MultiWhitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex NonNameChars = new("[^-A-Za-z0-9'� ]+", RegexOptions.Compiled);

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

    private static string NormalizeOcrLine(string line)
    {
        var normalized = line.Replace("�", "'").Replace('`', '\'');
        normalized = NonNameChars.Replace(normalized, " ");
        normalized = MultiWhitespace.Replace(normalized, " ").Trim();

        var parsed = ItemNameParser.ParseDetectedItem(normalized);
        if (!string.IsNullOrWhiteSpace(parsed.Name))
        {
            normalized = $"{parsed.Quantity}x {parsed.Name}";
        }

        return normalized.Trim(' ', '-', '\'', ',');
    }
}
