using System.Text.RegularExpressions;
using RuneshapePriceChecker.Pricing;

namespace RuneshapePriceChecker.OCR;

internal static class OcrTextPostProcessor
{
    private static readonly Regex MultiWhitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex NonNameChars = new("[^-A-Za-z0-9'� ]+", RegexOptions.Compiled);

    public static IReadOnlyList<string> ExtractLikelyItemNames(string rawText)
    {
        var lines = rawText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeOcrLine)
            .Where(line => line.Length >= 3)
            .Where(line => line.Any(char.IsLetter))
            .ToArray();

        return lines;
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
