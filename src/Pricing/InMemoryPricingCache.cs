using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using RuneshapePriceChecker.Contracts;

namespace RuneshapePriceChecker.Pricing;

public sealed class InMemoryPricingCache(IPoeNinjaClient poeNinjaClient) : IPricingCache
{
    private readonly ConcurrentDictionary<string, decimal> _exactPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> _fallbackPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (decimal MinChaos, decimal MaxChaos)> _uniqueCategoryRanges = new(StringComparer.OrdinalIgnoreCase);
    private decimal _divineOrbChaosValue = 150m;
    private static readonly Regex NonAlphaNumeric = new("[^A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex LeadingQuantityWithX = new("^(?:\\d+|[AaIiLlTt|])\\s*[Xx]\\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingQuantityWithoutX = new("^(?:\\d+|[IiLl|])\\s+", RegexOptions.Compiled);
    private static readonly Regex SplitPossessive = new("\\b([A-Z]+)\\s+[S5]\\s+([A-Z]+)\\b", RegexOptions.Compiled);
    private static readonly Regex ZeroForOInOrb = new("\\b0RB\\b", RegexOptions.Compiled);
    private static readonly Regex GForOInOrb = new("\\bGRB\\b", RegexOptions.Compiled);
    private static readonly Regex TrailingLevel = new("\\s+LEVEL\\s+\\d+$", RegexOptions.Compiled);
    private static readonly Regex TrailingOrb = new("\\s+ORB$", RegexOptions.Compiled);
    private static readonly Regex TieredOrb = new("^(?:GREATER|PERFECT)\\s+(ORB OF .+)$", RegexOptions.Compiled);
    private static readonly Regex TieredRune = new("^(?:GREATER|PERFECT)\\s+(.+\\s+RUNE)$", RegexOptions.Compiled);

    public PriceQuote? TryGetPriceQuote(string itemName)
    {
        return TryGetPriceQuote(itemName, 1);
    }

    public PriceQuote? TryGetPriceQuote(string itemName, int quantity)
    {
        var clampedQuantity = Math.Max(1, quantity);
        foreach (var key in BuildLookupCandidates(itemName))
        {
            if (_exactPrices.TryGetValue(key, out var exactChaosValue))
            {
                var totalChaosValue = exactChaosValue * clampedQuantity;
                return new PriceQuote(FormatAmount(totalChaosValue), totalChaosValue, false);
            }

            if (_fallbackPrices.TryGetValue(key, out var fallbackChaosValue))
            {
                var totalChaosValue = fallbackChaosValue * clampedQuantity;
                return new PriceQuote(FormatAmount(totalChaosValue), totalChaosValue, false);
            }

            if (TryResolveUniqueCategoryRange(key, out var range))
            {
                var minTotal = range.MinChaos * clampedQuantity;
                var maxTotal = range.MaxChaos * clampedQuantity;
                var label = $"{FormatAmount(minTotal)} - {FormatAmount(maxTotal)}";
                return new PriceQuote(label, maxTotal, true);
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildLookupCandidates(string itemName)
    {
        var normalized = Normalize(itemName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(normalized))
        {
            yield return normalized;
        }

        var withoutLevel = TrailingLevel.Replace(normalized, string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(withoutLevel) && seen.Add(withoutLevel))
        {
            yield return withoutLevel;
        }

        var withoutOrb = TrailingOrb.Replace(normalized, string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(withoutOrb) && seen.Add(withoutOrb))
        {
            yield return withoutOrb;
        }

        var withoutLevelAndOrb = TrailingOrb.Replace(withoutLevel, string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(withoutLevelAndOrb) && seen.Add(withoutLevelAndOrb))
        {
            yield return withoutLevelAndOrb;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var latest = await poeNinjaClient.FetchCurrentPricesAsync(cancellationToken).ConfigureAwait(false);

        _exactPrices.Clear();
        _fallbackPrices.Clear();
        foreach (var pair in latest.ExactPrices)
        {
            var normalized = Normalize(pair.Key);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            _exactPrices[normalized] = pair.Value;

            var tieredMatch = TieredOrb.Match(normalized);
            if (tieredMatch.Success)
            {
                var bareOrbName = tieredMatch.Groups[1].Value;
                if (_fallbackPrices.TryGetValue(bareOrbName, out var existing))
                {
                    _fallbackPrices[bareOrbName] = Math.Min(existing, pair.Value);
                }
                else
                {
                    _fallbackPrices[bareOrbName] = pair.Value;
                }
            }

            var tieredRuneMatch = TieredRune.Match(normalized);
            if (tieredRuneMatch.Success)
            {
                var bareRuneName = tieredRuneMatch.Groups[1].Value;
                if (_fallbackPrices.TryGetValue(bareRuneName, out var existing))
                {
                    _fallbackPrices[bareRuneName] = Math.Min(existing, pair.Value);
                }
                else
                {
                    _fallbackPrices[bareRuneName] = pair.Value;
                }
            }
        }

        _uniqueCategoryRanges.Clear();
        foreach (var pair in latest.UniqueCategoryRanges)
        {
            var normalized = Normalize(pair.Key);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            _uniqueCategoryRanges[normalized] = pair.Value;
        }

        if (latest.DivineOrbChaosValue > 0)
        {
            _divineOrbChaosValue = latest.DivineOrbChaosValue;
        }
    }

    private bool TryResolveUniqueCategoryRange(string normalizedItemName, out (decimal MinChaos, decimal MaxChaos) range)
    {
        range = default;

        if (_uniqueCategoryRanges.TryGetValue(normalizedItemName, out range))
        {
            return true;
        }

        if (!normalizedItemName.StartsWith("UNIQUE ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var categoryTail = normalizedItemName["UNIQUE ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(categoryTail))
        {
            return false;
        }

        var singularTail = categoryTail.EndsWith("S", StringComparison.OrdinalIgnoreCase)
            ? categoryTail[..^1]
            : categoryTail;

        var candidates = new[]
        {
            $"UNIQUE {categoryTail}",
            $"UNIQUE {singularTail}",
            $"UNIQUE {categoryTail.Replace("ARMOR", "ARMOUR", StringComparison.OrdinalIgnoreCase)}",
            $"UNIQUE {categoryTail.Replace("ARMOUR", "ARMOR", StringComparison.OrdinalIgnoreCase)}"
        };

        foreach (var candidate in candidates)
        {
            var normalizedCandidate = Normalize(candidate);
            if (_uniqueCategoryRanges.TryGetValue(normalizedCandidate, out range))
            {
                return true;
            }
        }

        return false;
    }

    private string FormatAmount(decimal chaosValue)
    {
        var chaos = Math.Max(0m, chaosValue);
        if (_divineOrbChaosValue > 0m && chaos >= _divineOrbChaosValue)
        {
            var divine = chaos / _divineOrbChaosValue;
            return $"{divine:0.#}d";
        }

        return $"{chaos:0.#}c";
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace('’', '\'')
            .Replace("'", string.Empty);

        normalized = LeadingQuantityWithX.Replace(normalized, string.Empty);
        normalized = LeadingQuantityWithoutX.Replace(normalized, string.Empty);
        normalized = NonAlphaNumeric.Replace(normalized, " ");
        normalized = normalized.Trim().ToUpperInvariant();
        normalized = SplitPossessive.Replace(normalized, "$1S $2");
        normalized = ZeroForOInOrb.Replace(normalized, "ORB");
        normalized = GForOInOrb.Replace(normalized, "ORB");

        return normalized;
    }
}
