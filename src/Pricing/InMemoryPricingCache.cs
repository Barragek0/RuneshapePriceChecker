using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;

namespace RuneshapePriceChecker.Pricing;

public sealed class InMemoryPricingCache(IPoeNinjaClient poeNinjaClient, IOptionsMonitor<PricingCacheOptions> pricingOptions) : IPricingCache
{
    private readonly ConcurrentDictionary<string, decimal> _exactPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> _fallbackPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (decimal MinChaos, decimal MaxChaos)> _uniqueCategoryRanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (decimal MinChaos, decimal MaxChaos)> _uncutGemRanges = new(StringComparer.OrdinalIgnoreCase);
    private decimal _divineOrbChaosValue = 150m;
    private decimal _exaltedOrbChaosValue;
    private static readonly Regex NonAlphaNumeric = new("[^A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex LeadingQuantityWithX = new("^(?:\\d+|[AaIiLlTt|])\\s*[Xx]\\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingQuantityWithoutX = new("^(?:\\d+|[IiLl|])\\s+", RegexOptions.Compiled);
    private static readonly Regex SplitPossessive = new("\\b([A-Z]+)\\s+[S5]\\s+([A-Z]+)\\b", RegexOptions.Compiled);
    private static readonly Regex TrailingLevelNumber = new("\\s+LEVEL\\s+(?<level>\\d+)$", RegexOptions.Compiled);
    private static readonly Regex TrailingLevel = new("\\s+LEVEL\\s+\\d+$", RegexOptions.Compiled);
    private static readonly Regex TrailingOrb = new("\\s+ORB$", RegexOptions.Compiled);

    public PriceQuote? TryGetPriceQuote(string itemName)
    {
        return TryGetPriceQuote(itemName, 1);
    }

    public PriceQuote? TryGetPriceQuote(string itemName, int quantity)
    {
        var clampedQuantity = Math.Max(1, quantity);
        var keys = BuildLookupCandidates(itemName).ToArray();
        foreach (var key in keys)
        {
            var quote = TryGetPriceQuoteForKey(key, clampedQuantity);
            if (quote is not null)
            {
                return quote;
            }
        }

        if (TryResolveSingleLetterOffCandidate(keys, out var correctedKey))
        {
            var correctedQuote = TryGetPriceQuoteForKey(correctedKey, clampedQuantity);
            if (correctedQuote is not null)
            {
                return correctedQuote with
                {
                    MatchDetail = $"Very close match triggered: {correctedKey}={correctedQuote.Label}"
                };
            }
        }

        return null;
    }

    private PriceQuote? TryGetPriceQuoteForKey(string key, int quantity)
    {
        if (_exactPrices.TryGetValue(key, out var exactChaosValue))
        {
            var totalChaosValue = exactChaosValue * quantity;
            return new PriceQuote(FormatAmount(totalChaosValue), totalChaosValue, false);
        }

        if (_fallbackPrices.TryGetValue(key, out var fallbackChaosValue))
        {
            var totalChaosValue = fallbackChaosValue * quantity;
            return new PriceQuote(FormatAmount(totalChaosValue), totalChaosValue, false);
        }

        if (TryResolveUniqueCategoryRange(key, out var range))
        {
            var minTotal = range.MinChaos * quantity;
            var maxTotal = range.MaxChaos * quantity;
            var label = $"{FormatAmount(minTotal)} - {FormatAmount(maxTotal)}";
            return new PriceQuote(label, maxTotal, true);
        }

        if (TryResolveUncutGemRange(key, out var gemRange))
        {
            var minTotal = gemRange.MinChaos * quantity;
            var maxTotal = gemRange.MaxChaos * quantity;
            var label = $"{FormatAmount(minTotal)} - {FormatAmount(maxTotal)}";
            return new PriceQuote(label, maxTotal, true);
        }

        return null;
    }

    private bool TryResolveSingleLetterOffCandidate(IReadOnlyList<string> keys, out string correctedKey)
    {
        correctedKey = string.Empty;
        var matchCount = 0;

        foreach (var key in keys)
        {
            if (key.Length < 7)
            {
                continue;
            }

            foreach (var known in EnumerateKnownLookupKeys())
            {
                if (!IsSingleSubstitutionAway(key, known))
                {
                    continue;
                }

                correctedKey = known;
                matchCount++;
                if (matchCount > 1)
                {
                    correctedKey = string.Empty;
                    return false;
                }
            }
        }

        return matchCount == 1;
    }

    private IEnumerable<string> EnumerateKnownLookupKeys()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in _exactPrices.Keys)
        {
            if (seen.Add(key))
            {
                yield return key;
            }
        }

        foreach (var key in _fallbackPrices.Keys)
        {
            if (seen.Add(key))
            {
                yield return key;
            }
        }

        foreach (var key in _uniqueCategoryRanges.Keys)
        {
            if (seen.Add(key))
            {
                yield return key;
            }
        }

        foreach (var key in _uncutGemRanges.Keys)
        {
            if (seen.Add(key))
            {
                yield return key;
            }
        }
    }

    private static bool IsSingleSubstitutionAway(string source, string candidate)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (source.Length != candidate.Length)
        {
            return false;
        }

        var differences = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == candidate[i])
            {
                continue;
            }

            differences++;
            if (differences > 1)
            {
                return false;
            }
        }

        return differences == 1;
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

        var levelMatch = TrailingLevelNumber.Match(normalized);
        if (levelMatch.Success)
        {
            var levelNumber = levelMatch.Groups["level"].Value;
            var withNumericLevel = TrailingLevelNumber.Replace(normalized, $" {levelNumber}").Trim();
            if (!string.IsNullOrWhiteSpace(withNumericLevel) && seen.Add(withNumericLevel))
            {
                yield return withNumericLevel;
            }
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

            if (PricingTextRules.TryGetTierFallbackKey(normalized, out var fallbackKey))
            {
                if (_fallbackPrices.TryGetValue(fallbackKey, out var existing))
                {
                    _fallbackPrices[fallbackKey] = Math.Min(existing, pair.Value);
                }
                else
                {
                    _fallbackPrices[fallbackKey] = pair.Value;
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

        RebuildUncutGemRanges();

        if (latest.DivineOrbChaosValue > 0)
        {
            _divineOrbChaosValue = latest.DivineOrbChaosValue;
        }

        if (latest.ExaltedOrbChaosValue > 0)
        {
            _exaltedOrbChaosValue = latest.ExaltedOrbChaosValue;
        }
    }

    private void RebuildUncutGemRanges()
    {
        _uncutGemRanges.Clear();

        foreach (var family in PricingTextRules.UncutGemFamilies)
        {
            decimal? min = null;
            decimal? max = null;

            foreach (var pair in _exactPrices)
            {
                if (!pair.Key.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                min = min.HasValue ? Math.Min(min.Value, pair.Value) : pair.Value;
                max = max.HasValue ? Math.Max(max.Value, pair.Value) : pair.Value;
            }

            if (min.HasValue && max.HasValue)
            {
                _uncutGemRanges[family] = (min.Value, max.Value);
            }
        }
    }

    private bool TryResolveUncutGemRange(string normalizedItemName, out (decimal MinChaos, decimal MaxChaos) range)
    {
        range = default;

        foreach (var family in PricingTextRules.UncutGemFamilies)
        {
            if (!normalizedItemName.StartsWith(family, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = normalizedItemName[family.Length..].Trim();
            if (suffix.Length > 0)
            {
                return false;
            }

            if (_uncutGemRanges.TryGetValue(family, out range))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveUniqueCategoryRange(string normalizedItemName, out (decimal MinChaos, decimal MaxChaos) range)
    {
        range = default;

        if (_uniqueCategoryRanges.TryGetValue(normalizedItemName, out range))
        {
            return true;
        }

        if (TryResolveCombinedJewelleryRange(normalizedItemName, out range))
        {
            return true;
        }

        foreach (var candidate in PricingTextRules.BuildUniqueCategoryLookupCandidates(normalizedItemName))
        {
            var normalizedCandidate = Normalize(candidate);
            if (_uniqueCategoryRanges.TryGetValue(normalizedCandidate, out range))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveCombinedJewelleryRange(string normalizedItemName, out (decimal MinChaos, decimal MaxChaos) range)
    {
        range = default;

        if (!normalizedItemName.Equals("UNIQUE JEWELLERY", StringComparison.OrdinalIgnoreCase) &&
            !normalizedItemName.Equals("UNIQUE JEWELRY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        decimal? min = null;
        decimal? max = null;

        foreach (var pair in _uniqueCategoryRanges)
        {
            var key = pair.Key;
            if (!key.StartsWith("UNIQUE ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!key.Contains("RING", StringComparison.OrdinalIgnoreCase) &&
                !key.Contains("AMULET", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            min = min.HasValue ? Math.Min(min.Value, pair.Value.MinChaos) : pair.Value.MinChaos;
            max = max.HasValue ? Math.Max(max.Value, pair.Value.MaxChaos) : pair.Value.MaxChaos;
        }

        if (!min.HasValue || !max.HasValue)
        {
            return false;
        }

        range = (min.Value, max.Value);
        return true;
    }

    private string FormatAmount(decimal chaosValue)
    {
        return PricingTextRules.FormatAmount(
            chaosValue,
            _divineOrbChaosValue,
            _exaltedOrbChaosValue,
            pricingOptions.CurrentValue.DisplayCurrency);
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
        normalized = PricingTextRules.ApplyNormalizedWordSwaps(normalized);

        return normalized;
    }
}
