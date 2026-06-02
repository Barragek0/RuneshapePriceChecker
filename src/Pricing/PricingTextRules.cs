using System.Globalization;
using System.Text.RegularExpressions;

namespace RuneshapePriceChecker.Pricing;

public static class PricingTextRules
{
    private static readonly Regex QuantityPrefixWithX = new("^(?<quantity>\\d+|[AaIiLlTt|Oo0])\\s*[xX]\\s+(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex QuantityPrefixWithoutX = new("^(?<quantity>\\d+|[IiLl|Oo0])\\s+(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex IsolatedImToken = new("(?<=\\s)im(?=\\s)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MultiWhitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex TieredOrb = new("^(?:GREATER|PERFECT)\\s+(ORB OF .+)$", RegexOptions.Compiled);
    private static readonly Regex TieredRune = new("^(?:GREATER|PERFECT)\\s+(.+\\s+RUNE)$", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> OcrQuantityTokenMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1,
        ["i"] = 1,
        ["l"] = 1,
        ["t"] = 1,
        ["|"] = 1,
        ["o"] = 2,
        ["0"] = 2
    };

    private static readonly Dictionary<string, string> NormalizedWordSwaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0RB"] = "ORB",
        ["GRB"] = "ORB",
        ["LRON"] = "IRON"
    };

    private static readonly Dictionary<string, string[]> IdAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gcp"] = ["Gemcutter's Prism"],
        ["bauble"] = ["Glassblower's Bauble"],
        ["etcher"] = ["Arcanist's Etcher"],
        ["aug"] = ["Orb of Augmentation"],
        ["alch"] = ["Orb of Alchemy"],
        ["transmute"] = ["Orb of Transmutation"],
        ["regal"] = ["Regal Orb"],
        ["chaos"] = ["Chaos Orb"],
        ["divine"] = ["Divine Orb"],
        ["exalted"] = ["Exalted Orb"],
        ["annul"] = ["Orb of Annulment"],
        ["artificers"] = ["Artificer's Orb"],
        ["chance"] = ["Orb of Chance"],
        ["mirror"] = ["Mirror of Kalandra"],
        ["scrap"] = ["Armourer's Scrap"],
        ["vaal"] = ["Vaal Orb"],
        ["whetstone"] = ["Blacksmith's Whetstone"],
        ["wisdom"] = ["Scroll of Wisdom"]
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

        var cleanedRaw = MultiWhitespace.Replace(IsolatedImToken.Replace(raw, string.Empty), " ").Trim();
        if (string.IsNullOrWhiteSpace(cleanedRaw))
        {
            return new ParsedDetectedItem(string.Empty, 1);
        }

        var match = QuantityPrefixWithX.Match(cleanedRaw);
        if (!match.Success)
        {
            match = QuantityPrefixWithoutX.Match(cleanedRaw);
        }

        if (!match.Success)
        {
            return new ParsedDetectedItem(cleanedRaw, 1);
        }

        var rawQuantity = match.Groups["quantity"].Value;
        var quantity = NormalizeQuantityToken(rawQuantity);

        var name = match.Groups["name"].Value.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ParsedDetectedItem(cleanedRaw, 1);
        }

        return new ParsedDetectedItem(name, quantity);
    }

    public static int NormalizeQuantityToken(string rawQuantity)
    {
        if (string.IsNullOrWhiteSpace(rawQuantity))
        {
            return 1;
        }

        if (int.TryParse(rawQuantity, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        var token = rawQuantity.Trim();
        if (OcrQuantityTokenMap.TryGetValue(token, out var mapped) && mapped > 0)
        {
            return mapped;
        }

        return 1;
    }

    public static string ApplyNormalizedWordSwaps(string normalizedUpperText)
    {
        if (string.IsNullOrWhiteSpace(normalizedUpperText))
        {
            return string.Empty;
        }

        var parts = normalizedUpperText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (NormalizedWordSwaps.TryGetValue(parts[i], out var replacement))
            {
                parts[i] = replacement;
            }
        }

        return string.Join(' ', parts);
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

    public static IEnumerable<string> BuildUniqueCategoryLookupCandidates(string normalizedItemName)
    {
        if (string.IsNullOrWhiteSpace(normalizedItemName) ||
            !normalizedItemName.StartsWith("UNIQUE ", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var categoryTail = normalizedItemName["UNIQUE ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(categoryTail))
        {
            yield break;
        }

        var singularTail = categoryTail.EndsWith("S", StringComparison.OrdinalIgnoreCase)
            ? categoryTail[..^1]
            : categoryTail;

        var candidateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"UNIQUE {categoryTail}",
            $"UNIQUE {singularTail}",
            $"UNIQUE {categoryTail.Replace("ARMOR", "ARMOUR", StringComparison.OrdinalIgnoreCase)}",
            $"UNIQUE {categoryTail.Replace("ARMOUR", "ARMOR", StringComparison.OrdinalIgnoreCase)}"
        };

        AddAccessoryFamilyVariants(candidateSet, categoryTail);
        AddAccessoryFamilyVariants(candidateSet, singularTail);

        foreach (var candidate in candidateSet)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate;
            }
        }
    }

    public static string FormatAmount(
        decimal chaosValue,
        decimal divineOrbChaosValue,
        decimal exaltedOrbChaosValue,
        string? displayCurrency)
    {
        var chaos = Math.Max(0m, chaosValue);

        var useExaltedDisplay =
            string.Equals(displayCurrency, "exalt", StringComparison.OrdinalIgnoreCase) &&
            exaltedOrbChaosValue > 0m;

        if (useExaltedDisplay)
        {
            if (divineOrbChaosValue > 0m && chaos >= divineOrbChaosValue)
            {
                var divine = TruncateToSingleDecimal(chaos / divineOrbChaosValue);
                return $"{divine.ToString("0.#", CultureInfo.InvariantCulture)}d";
            }

            var exalted = TruncateToSingleDecimal(chaos / exaltedOrbChaosValue);
            if (chaos > 0m && exalted <= 0m)
            {
                return "<0.1ex";
            }

            return $"{exalted.ToString("0.#", CultureInfo.InvariantCulture)}ex";
        }

        if (divineOrbChaosValue > 0m && chaos >= divineOrbChaosValue)
        {
            var divine = TruncateToSingleDecimal(chaos / divineOrbChaosValue);
            return $"{divine.ToString("0.#", CultureInfo.InvariantCulture)}d";
        }

        var truncatedChaos = TruncateToSingleDecimal(chaos);
        if (chaos > 0m && truncatedChaos <= 0m)
        {
            return "<0.1c";
        }

        return $"{truncatedChaos.ToString("0.#", CultureInfo.InvariantCulture)}c";
    }

    private static decimal TruncateToSingleDecimal(decimal value)
    {
        return Math.Truncate(value * 10m) / 10m;
    }

    private static void AddAccessoryFamilyVariants(HashSet<string> candidateSet, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var toAccessoriesPlural = value
            .Replace("JEWELLERY", "ACCESSORIES", StringComparison.OrdinalIgnoreCase)
            .Replace("JEWELRY", "ACCESSORIES", StringComparison.OrdinalIgnoreCase)
            .Replace("RINGS", "ACCESSORIES", StringComparison.OrdinalIgnoreCase)
            .Replace("RING", "ACCESSORY", StringComparison.OrdinalIgnoreCase)
            .Replace("AMULETS", "ACCESSORIES", StringComparison.OrdinalIgnoreCase)
            .Replace("AMULET", "ACCESSORY", StringComparison.OrdinalIgnoreCase);

        var toAccessoriesSingular = toAccessoriesPlural
            .Replace("ACCESSORIES", "ACCESSORY", StringComparison.OrdinalIgnoreCase);

        var toJewellery = value
            .Replace("ACCESSORIES", "JEWELLERY", StringComparison.OrdinalIgnoreCase)
            .Replace("ACCESSORY", "JEWELLERY", StringComparison.OrdinalIgnoreCase);

        candidateSet.Add($"UNIQUE {toAccessoriesPlural}");
        candidateSet.Add($"UNIQUE {toAccessoriesSingular}");
        candidateSet.Add($"UNIQUE {toJewellery}");
    }
}

public readonly record struct ParsedDetectedItem(string Name, int Quantity);