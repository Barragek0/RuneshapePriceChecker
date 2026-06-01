using System.Globalization;
using System.Text.RegularExpressions;

namespace RuneshapePriceChecker.Pricing;

public static class PricingTextRules
{
    private static readonly Regex QuantityPrefixWithX = new("^(?<quantity>\\d+|[AaIiLlTt|])\\s*[xX]\\s+(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex QuantityPrefixWithoutX = new("^(?<quantity>\\d+|[IiLl|])\\s+(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex TieredOrb = new("^(?:GREATER|PERFECT)\\s+(ORB OF .+)$", RegexOptions.Compiled);
    private static readonly Regex TieredRune = new("^(?:GREATER|PERFECT)\\s+(.+\\s+RUNE)$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string[]> IdAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gcp"] = ["Gemcutter's Prism"],
        ["bauble"] = ["Glassblower's Bauble"],
        ["etcher"] = ["Arcanist's Etcher"],
        ["aug"] = ["Orb of Augmentation"],
        ["transmute"] = ["Orb of Transmutation"],
        ["regal"] = ["Regal Orb"],
        ["chaos"] = ["Chaos Orb"],
        ["divine"] = ["Divine Orb"],
        ["exalted"] = ["Exalted Orb"]
    };

    public static readonly string[] UncutGemFamilies =
    [
        "UNCUT SUPPORT GEM",
        "UNCUT SKILL GEM",
        "UNCUT SPIRIT GEM"
    ];

    public static ParsedDetectedItem ParseDetectedItem(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ParsedDetectedItem(string.Empty, 1);
        }

        var match = QuantityPrefixWithX.Match(raw);
        if (!match.Success)
        {
            match = QuantityPrefixWithoutX.Match(raw);
        }

        if (!match.Success)
        {
            return new ParsedDetectedItem(raw.Trim(), 1);
        }

        var rawQuantity = match.Groups["quantity"].Value;
        var quantity = rawQuantity switch
        {
            "a" or "A" or "i" or "I" or "l" or "L" or "t" or "T" or "|" => 1,
            _ when int.TryParse(rawQuantity, out var parsed) && parsed > 0 => parsed,
            _ => 1
        };

        var name = match.Groups["name"].Value.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ParsedDetectedItem(raw.Trim(), 1);
        }

        return new ParsedDetectedItem(name, quantity);
    }

    public static IEnumerable<string> ExpandIdAliases(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            yield break;
        }

        if (IdAliases.TryGetValue(id, out var aliases))
        {
            foreach (var alias in aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    yield return alias;
                }
            }
        }
    }

    public static bool TryGetTierFallbackKey(string normalizedItemName, out string fallbackKey)
    {
        fallbackKey = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedItemName))
        {
            return false;
        }

        var orbMatch = TieredOrb.Match(normalizedItemName);
        if (orbMatch.Success)
        {
            fallbackKey = orbMatch.Groups[1].Value;
            return true;
        }

        var runeMatch = TieredRune.Match(normalizedItemName);
        if (runeMatch.Success)
        {
            fallbackKey = runeMatch.Groups[1].Value;
            return true;
        }

        return false;
    }

    public static string FormatAmount(decimal chaosValue, decimal divineOrbChaosValue)
    {
        var chaos = Math.Max(0m, chaosValue);
        if (divineOrbChaosValue > 0m && chaos >= divineOrbChaosValue)
        {
            var divine = TruncateToSingleDecimal(chaos / divineOrbChaosValue);
            return $"{divine.ToString("0.#", CultureInfo.InvariantCulture)}d";
        }

        var truncatedChaos = TruncateToSingleDecimal(chaos);
        return $"{truncatedChaos.ToString("0.#", CultureInfo.InvariantCulture)}c";
    }

    private static decimal TruncateToSingleDecimal(decimal value)
    {
        return Math.Truncate(value * 10m) / 10m;
    }
}

public readonly record struct ParsedDetectedItem(string Name, int Quantity);