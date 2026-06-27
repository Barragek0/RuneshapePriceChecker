using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;

namespace RuneshapePriceChecker.Pricing;

public sealed class InMemoryPricingCache(
    IPricingSource pricingSource,
    IOptionsMonitor<PricingCacheOptions> pricingOptions,
    ILogger<InMemoryPricingCache> logger,
    ItemNameTranslator? translator = null)
{
    private readonly ConcurrentDictionary<string, decimal> _exactPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> _fallbackPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (decimal MinChaos, decimal MaxChaos)> _uniqueCategoryRanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (decimal MinChaos, decimal MaxChaos)> _uncutGemRanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _uniqueItemBaseTypes = new(StringComparer.OrdinalIgnoreCase);
    private decimal _divineOrbChaosValue = 150m;
    private decimal _exaltedOrbChaosValue;
    private decimal _currencyMinChaos;
    private decimal _currencyMaxChaos;
    private volatile bool _ready;

    public bool IsReady => _ready;

    public void SetOcrLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);

        translator?.SetLanguage(language);
        if (translator is not null && !translator.IsLoaded && !language.Equals("eng", StringComparison.OrdinalIgnoreCase))
        {
            translator.LoadAsync(language, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
    private static readonly Regex NonAlphaNumeric = new("[^\\p{L}\\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex LeadingQuantityWithX = new("^(?:\\d+|[AaIiLlTt|])\\s*[Xx]\\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingQuantityWithoutX = new("^(?:\\d+|[IiLl|])\\s+", RegexOptions.Compiled);
    private static readonly Regex SplitPossessive = new("\\b([\\p{L}]+)\\s+[S5]\\s+([\\p{L}]+)\\b", RegexOptions.Compiled);
    private static readonly Regex TrailingLevelNumber = new("\\s+LEVEL\\s+(?<level>\\d+)$", RegexOptions.Compiled);
    private static readonly Regex TrailingLevel = new("\\s+LEVEL\\s+\\d+$", RegexOptions.Compiled);
    private static readonly Regex TrailingOrb = new("\\s+ORB$", RegexOptions.Compiled);

    public PriceQuote? TryGetPriceQuote(string itemName) => TryGetPriceQuote(itemName, 1);

    public PriceQuote? TryGetPriceQuote(string itemName, int quantity)
    {
        var clampedQuantity = Math.Max(1, quantity);
        var translatedName = translator?.ToEnglish(itemName) ?? itemName;
        var keys = BuildLookupCandidates(translatedName);
        logger.LogTrace("PriceLookup: '{Name}' translated='{T}' keys=[{Keys}]", itemName, translatedName, string.Join(", ", keys));
        foreach (var key in keys)
        {
            var quote = TryGetPriceQuoteForKey(key, clampedQuantity);
            if (quote is not null)
            {
                logger.LogTrace("PriceLookup: '{Name}' matched key '{Key}' -> {Label}", itemName, key, quote.Label);
                return quote;
            }
        }

        if (itemName.StartsWith("Unique ", StringComparison.OrdinalIgnoreCase))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Quote: {Count} fail '{Name}'", keys.Length, itemName);
            }
        }

        // Fallback: try single-character substitution via FallbackProvider
        if (FallbackProvider.TryResolveSingleLetterOffCandidate(keys, EnumerateKnownLookupKeys(), out var correctedKey))
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

        // Fallback: try 2-character substitution via FallbackProvider
        var fewAwayKey = FallbackProvider.ResolveFewCharsAwayCandidate(keys, EnumerateKnownLookupKeys());
        if (fewAwayKey is not null)
        {
            var fewAwayQuote = TryGetPriceQuoteForKey(fewAwayKey, clampedQuantity);
            if (fewAwayQuote is not null)
            {
                return fewAwayQuote with
                {
                    MatchDetail = $"Few chars match: {fewAwayKey}={fewAwayQuote.Label}"
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
            return new PriceQuote(FormatAmount(totalChaosValue), totalChaosValue, false)
            {
                MatchDetail = $"exact: {key}={FormatAmount(exactChaosValue)}"
            };
        }

        if (TryResolveRandomCurrency(key, quantity, out var currencyQuote) && currencyQuote is not null)
        {
            return currencyQuote with
            {
                MatchDetail = $"random currency: {key}={currencyQuote.Label}"
            };
        }

        if (_fallbackPrices.TryGetValue(key, out var fallbackChaosValue))
        {
            var totalChaosValue = fallbackChaosValue * quantity;
            return new PriceQuote(FormatAmount(totalChaosValue), totalChaosValue, false)
            {
                MatchDetail = $"fallback: {key}={FormatAmount(fallbackChaosValue)}"
            };
        }

        if (TryResolveUniqueCategoryRange(key, out var range))
        {
            var minTotal = range.MinChaos * quantity;
            var maxTotal = range.MaxChaos * quantity;
            var label = $"{FormatAmount(minTotal)} - {FormatAmount(maxTotal)}";
            return new PriceQuote(label, maxTotal, true)
            {
                MatchDetail = $"unique range: {key}={FormatAmount(range.MinChaos)}-{FormatAmount(range.MaxChaos)}"
            };
        }

        if (TryResolveUncutGemRange(key, out var gemRange))
        {
            var minTotal = gemRange.MinChaos * quantity;
            var maxTotal = gemRange.MaxChaos * quantity;
            var label = $"{FormatAmount(minTotal)} - {FormatAmount(maxTotal)}";
            return new PriceQuote(label, maxTotal, true)
            {
                MatchDetail = $"gem range: {key}={FormatAmount(gemRange.MinChaos)}-{FormatAmount(gemRange.MaxChaos)}"
            };
        }

        return null;
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

    private static string[] BuildLookupCandidates(string itemName)
    {
        var normalized = Normalize(itemName);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var list = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                list.Add(candidate);
        }

        TryAdd(normalized);

        var levelMatch = TrailingLevelNumber.Match(normalized);
        if (levelMatch.Success)
        {
            var levelNumber = levelMatch.Groups["level"].Value;
            var withNumericLevel = TrailingLevelNumber.Replace(normalized, $" {levelNumber}").Trim();
            TryAdd(withNumericLevel);
        }

        var withoutLevel = TrailingLevel.Replace(normalized, string.Empty).Trim();
        TryAdd(withoutLevel);

        var withoutOrb = TrailingOrb.Replace(normalized, string.Empty).Trim();
        TryAdd(withoutOrb);

        var withoutLevelAndOrb = TrailingOrb.Replace(withoutLevel, string.Empty).Trim();
        TryAdd(withoutLevelAndOrb);

        return [.. list];
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var league = pricingOptions.CurrentValue.League;
        var latest = await pricingSource.FetchPricesAsync(league, cancellationToken).ConfigureAwait(false);

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

            // Diagnostic: log UNCUT SKILL GEM price keys to verify level matching
            if (normalized.StartsWith("UNCUT SKILL GEM", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogTrace("PriceKey: raw='{Raw}' normalized='{Norm}' value={Val}", pair.Key, normalized, pair.Value);
            }

            if (ItemNameParser.TryGetTierFallbackKey(normalized, out var fallbackKey))
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

        var savedAggregateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _uniqueCategoryRanges.Keys)
        {
            if (key.StartsWith("UNIQUE ", StringComparison.OrdinalIgnoreCase))
                _ = savedAggregateKeys.Add(key);
        }
        var savedAggregates = new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in savedAggregateKeys)
        {
            if (_uniqueCategoryRanges.TryGetValue(key, out var range))
                savedAggregates[key] = range;
        }

        _uniqueCategoryRanges.Clear();
        _uniqueItemBaseTypes.Clear();
        foreach (var pair in latest.UniqueCategoryRanges)
        {
            var normalized = Normalize(pair.Key);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            _uniqueCategoryRanges[normalized] = pair.Value;
        }
        if (latest.UniqueItemBaseTypes is not null)
        {
            foreach (var pair in latest.UniqueItemBaseTypes)
            {
                var normalized = Normalize(pair.Key);
                if (!string.IsNullOrWhiteSpace(normalized) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    _uniqueItemBaseTypes[normalized] = pair.Value;
                }
            }
        }

        RebuildUniqueCategoryAggregates();

        foreach (var pair in savedAggregates)
        {
            if (!_uniqueCategoryRanges.ContainsKey(pair.Key))
                _uniqueCategoryRanges[pair.Key] = pair.Value;
        }

        RebuildUncutGemRanges();

        _divineOrbChaosValue = latest.DivineOrbChaosValue;
        _exaltedOrbChaosValue = latest.ExaltedOrbChaosValue;
        _currencyMinChaos = latest.CurrencyMinChaos;
        _currencyMaxChaos = latest.CurrencyMaxChaos;
        _ready = true;
    }

    private void RebuildUncutGemRanges()
    {
        _uncutGemRanges.Clear();

        foreach (var family in ItemNameParser.UncutGemFamilies)
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

    private void RebuildUniqueCategoryAggregates()
    {
        if (_uniqueCategoryRanges.IsEmpty) return;

        decimal? allMin = null, allMax = null;
        decimal? ringMin = null, ringMax = null;
        decimal? amuletMin = null, amuletMax = null;
        decimal? beltMin = null, beltMax = null;
        decimal? jewelleryMin = null, jewelleryMax = null;
        decimal? bodyMin = null, bodyMax = null;
        decimal? helmetMin = null, helmetMax = null;
        decimal? glovesMin = null, glovesMax = null;
        decimal? bootsMin = null, bootsMax = null;
        decimal? weaponMin = null, weaponMax = null;

        // Per-specific-category aggregates: "UNIQUE ONE HAND MACE", "UNIQUE BOW", etc.
        var perCategoryMin = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var perCategoryMax = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in _uniqueCategoryRanges)
        {
            var key = pair.Key;
            var (MinChaos, MaxChaos) = pair.Value;
            allMin = allMin.HasValue ? Math.Min(allMin.Value, MinChaos) : MinChaos;
            allMax = allMax.HasValue ? Math.Max(allMax.Value, MaxChaos) : MaxChaos;

            var isRing = key.Contains("RING", StringComparison.OrdinalIgnoreCase);
            var isAmulet = key.Contains("AMULET", StringComparison.OrdinalIgnoreCase);
            var isBelt = key.Contains("BELT", StringComparison.OrdinalIgnoreCase) || key.Contains("SASH", StringComparison.OrdinalIgnoreCase);

            if (isRing) { ringMin = Min(ringMin, MinChaos); ringMax = Max(ringMax, MaxChaos); }
            if (isAmulet) { amuletMin = Min(amuletMin, MinChaos); amuletMax = Max(amuletMax, MaxChaos); }
            if (isBelt) { beltMin = Min(beltMin, MinChaos); beltMax = Max(beltMax, MaxChaos); }
            if (isRing || isAmulet || isBelt) { jewelleryMin = Min(jewelleryMin, MinChaos); jewelleryMax = Max(jewelleryMax, MaxChaos); }

            // Per-specific-category aggregate: map item name to its unique category (e.g. "ONE HAND MACE")
            // Falls back to the base-type slot (HELMET, GLOVES, etc.) for items not in the explicit lookup.
            var specificCategory = UniqueItemTypeLookup.TryGetCategory(key);

            if (!_uniqueItemBaseTypes.TryGetValue(key, out var baseType))
                continue;

            var slot = GetSlotFromBaseType(baseType);

            // Use explicit category if available, otherwise use the slot from base type
            var categoryForAggregate = specificCategory ?? slot;
            if (categoryForAggregate is not null)
            {
                perCategoryMin[categoryForAggregate] = perCategoryMin.TryGetValue(categoryForAggregate, out var existingCMin)
                    ? Math.Min(existingCMin, MinChaos) : MinChaos;
                perCategoryMax[categoryForAggregate] = perCategoryMax.TryGetValue(categoryForAggregate, out var existingCMax)
                    ? Math.Max(existingCMax, MaxChaos) : MaxChaos;
            }

            switch (slot)
            {
                case "BODY ARMOUR": bodyMin = Min(bodyMin, MinChaos); bodyMax = Max(bodyMax, MaxChaos); break;
                case "HELMET": helmetMin = Min(helmetMin, MinChaos); helmetMax = Max(helmetMax, MaxChaos); break;
                case "GLOVES": glovesMin = Min(glovesMin, MinChaos); glovesMax = Max(glovesMax, MaxChaos); break;
                case "BOOTS": bootsMin = Min(bootsMin, MinChaos); bootsMax = Max(bootsMax, MaxChaos); break;
                case "WEAPON": weaponMin = Min(weaponMin, MinChaos); weaponMax = Max(weaponMax, MaxChaos); break;
                default:
                    break;
            }
        }

        // Per-specific-category aggregates: store them first so exact lookups win
        foreach (var kvp in perCategoryMin)
        {
            var category = kvp.Key;
            var min = kvp.Value;
            var max = perCategoryMax[category];
            _uniqueCategoryRanges[$"UNIQUE {category}"] = (min, max);
            if (category.Equals("BODY ARMOUR", StringComparison.OrdinalIgnoreCase))
                _uniqueCategoryRanges[$"UNIQUE BODY ARMOR"] = (min, max);
        }

        if (allMin.HasValue && allMax.HasValue)
            _uniqueCategoryRanges["UNIQUE"] = (allMin.Value, allMax.Value);
        if (ringMin.HasValue && ringMax.HasValue)
            _uniqueCategoryRanges["UNIQUE RING"] = (ringMin.Value, ringMax.Value);
        if (amuletMin.HasValue && amuletMax.HasValue)
            _uniqueCategoryRanges["UNIQUE AMULET"] = (amuletMin.Value, amuletMax.Value);
        if (beltMin.HasValue && beltMax.HasValue)
            _uniqueCategoryRanges["UNIQUE BELT"] = (beltMin.Value, beltMax.Value);
        if (jewelleryMin.HasValue && jewelleryMax.HasValue)
        {
            _uniqueCategoryRanges["UNIQUE JEWELLERY"] = (jewelleryMin.Value, jewelleryMax.Value);
            logger.LogDebug("Added UNIQUE JEWELLERY range: {Min:F1}-{Max:F1}", jewelleryMin.Value, jewelleryMax.Value);
        }
        if (bodyMin.HasValue && bodyMax.HasValue)
        {
            _uniqueCategoryRanges["UNIQUE BODY ARMOUR"] = (bodyMin.Value, bodyMax.Value);
            _uniqueCategoryRanges["UNIQUE BODY ARMOR"] = (bodyMin.Value, bodyMax.Value);
            logger.LogDebug("Added UNIQUE BODY ARMOUR range: {Min:F1}-{Max:F1}", bodyMin.Value, bodyMax.Value);
        }
        if (helmetMin.HasValue && helmetMax.HasValue)
        {
            _uniqueCategoryRanges["UNIQUE HELMET"] = (helmetMin.Value, helmetMax.Value);
            logger.LogDebug("Added UNIQUE HELMET range: {Min:F1}-{Max:F1}", helmetMin.Value, helmetMax.Value);
        }
        if (glovesMin.HasValue && glovesMax.HasValue)
        {
            _uniqueCategoryRanges["UNIQUE GLOVES"] = (glovesMin.Value, glovesMax.Value);
            logger.LogDebug("Added UNIQUE GLOVES range: {Min:F1}-{Max:F1}", glovesMin.Value, glovesMax.Value);
        }
        if (bootsMin.HasValue && bootsMax.HasValue)
        {
            _uniqueCategoryRanges["UNIQUE BOOTS"] = (bootsMin.Value, bootsMax.Value);
            logger.LogDebug("Added UNIQUE BOOTS range: {Min:F1}-{Max:F1}", bootsMin.Value, bootsMax.Value);
        }
        if (weaponMin.HasValue && weaponMax.HasValue)
        {
            _uniqueCategoryRanges["UNIQUE WEAPON"] = (weaponMin.Value, weaponMax.Value);
            logger.LogDebug("Added UNIQUE WEAPON range: {Min:F1}-{Max:F1}", weaponMin.Value, weaponMax.Value);
        }
    }

    private static string? GetSlotFromBaseType(string baseType)
    {
        var upper = baseType.ToUpperInvariant();

        if (upper.Contains("RING"))
            return "RING";

        if (upper.Contains("AMULET"))
            return "AMULET";

        if (upper.Contains("TALISMAN"))
            return "TALISMAN";

        if (upper.Contains("BELT") || upper.Contains("SASH"))
            return "BELT";

        if (upper.Contains("SHIELD"))
            return "SHIELD";

        if (upper.Contains("FOCUS"))
            return "FOCUS";

        if (upper.Contains("QUIVER"))
            return "QUIVER";

        if (upper.Contains("HELMET") || upper.Contains("HOOD") || upper.Contains("MASK") || upper.Contains("CROWN") ||
            upper.Contains("HELM") || upper.Contains("SALLET") || upper.Contains("BURGONET") || upper.Contains("COWL") ||
            upper.Contains("VISAGE") || upper.Contains("PELT") || upper.Contains("VEIL") || upper.Contains("BROW") ||
            upper.Contains("CREST") || upper.Contains("NOUS") || upper.Contains("CIRCLET") || upper.Contains("BASCINET") ||
            upper.Contains("GREATHELM") || upper.Contains("CASQUE") || upper.Contains("FACEGUARD"))
            return "HELMET";

        if (upper.Contains("GLOVES") || upper.Contains("MITTS") || upper.Contains("GAUNTLETS") || upper.Contains("MITT") ||
            upper.Contains("GRASP") || upper.Contains("FIST") || upper.Contains("GRIP") || upper.Contains("BRACER") ||
            upper.Contains("VAMBRACE") || upper.Contains("BINDING"))
            return "GLOVES";

        if (upper.Contains("BOOTS") || upper.Contains("SHOES") || upper.Contains("GREAVES") || upper.Contains("SLIPPERS") ||
            upper.Contains("BOOT") || upper.Contains("SOLE") || upper.Contains("TREAD") || upper.Contains("SABATON") ||
            upper.Contains("SANDALS") || upper.Contains("CLOGS") || upper.Contains("STRIDE") || upper.Contains("FOOT"))
            return "BOOTS";

        if (upper.Contains("ROBE") || upper.Contains("GARMENT") || upper.Contains("PLATE") || upper.Contains("VEST") ||
            upper.Contains("COAT") || upper.Contains("JACKET") || upper.Contains("TUNIC") || upper.Contains("WRAP") ||
            upper.Contains("CASSOCK") || upper.Contains("CUIRASS") || upper.Contains("HAUBERK") || upper.Contains("BRIGANDINE") ||
            upper.Contains("GAMBESON") || upper.Contains("DOUBLET") || upper.Contains("JERKIN") || upper.Contains("GARB") ||
            upper.Contains("RAIMENT") || upper.Contains("MANTLE") || upper.Contains("CHAINMAIL") || upper.Contains("SCALEMAIL") ||
            upper.Contains("RINGMAIL") || upper.Contains("CHESTPIECE") || upper.Contains("LEATHERVEST"))
            return "BODY ARMOUR";

        if (upper.Contains("SWORD") || upper.Contains("AXE") || upper.Contains("BOW") || upper.Contains("WAND") ||
            upper.Contains("STAFF") || upper.Contains("DAGGER") || upper.Contains("MACE") || upper.Contains("SCEPTRE") ||
            upper.Contains("CLAW") || upper.Contains("FLAIL") || upper.Contains("HAMMER") || upper.Contains("SPEAR") ||
            upper.Contains("ROD") || upper.Contains("LANCE") || upper.Contains("PIKE") || upper.Contains("CROSSBOW") ||
            upper.Contains("BLADE") || upper.Contains("SHANK") || upper.Contains("HATCHET") || upper.Contains("FORK") ||
            upper.Contains("MAUL") || upper.Contains("GREATSWORD") || upper.Contains("LONGSWORD") || upper.Contains("RAPIER") ||
            upper.Contains("SABRE") || upper.Contains("SICKLE") || upper.Contains("SCYTHE") || upper.Contains("CLUB") ||
            upper.Contains("WARHAMMER") || upper.Contains("QUARTERSTAFF") || upper.Contains("SHORTSWORD"))
            return "WEAPON";

        return null;
    }

    private static decimal? Min(decimal? a, decimal b)
    {
        return a.HasValue ? Math.Min(a.Value, b) : b;
    }

    private static decimal? Max(decimal? a, decimal b)
    {
        return a.HasValue ? Math.Max(a.Value, b) : b;
    }

    private bool TryResolveUncutGemRange(string normalizedItemName, out (decimal MinChaos, decimal MaxChaos) range)
    {
        range = default;

        foreach (var family in ItemNameParser.UncutGemFamilies)
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

    private bool TryResolveRandomCurrency(string key, int quantity, out PriceQuote? quote)
    {
        quote = null;
        if (!key.Equals("RANDOM CURRENCY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_currencyMinChaos <= 0m || _currencyMaxChaos <= 0m)
        {
            return false;
        }

        var minTotal = _currencyMinChaos * quantity;
        var maxTotal = _currencyMaxChaos * quantity;
        var label = $"{FormatAmount(minTotal)} - {FormatAmount(maxTotal)}";
        quote = new PriceQuote(label, maxTotal, true);
        return true;
    }

    private bool TryResolveUniqueCategoryRange(string normalizedItemName, out (decimal MinChaos, decimal MaxChaos) range)
    {
        range = default;

        if (_uniqueCategoryRanges.TryGetValue(normalizedItemName, out range))
        {
            logger.LogDebug("UniqueRange: hit '{Key}' {Min:F1}-{Max:F1}", normalizedItemName, range.MinChaos, range.MaxChaos);
            return true;
        }

        foreach (var candidate in ItemNameParser.BuildUniqueCategoryLookupCandidates(normalizedItemName))
        {
            var normalizedCandidate = Normalize(candidate);
            logger.LogDebug("UniqueRange: '{Key}'->'{Norm}'={Found}", normalizedItemName, normalizedCandidate, _uniqueCategoryRanges.ContainsKey(normalizedCandidate));
            if (_uniqueCategoryRanges.TryGetValue(normalizedCandidate, out range))
            {
                return true;
            }
        }

        return false;
    }

    private string FormatAmount(decimal chaosValue)
    {
        return ItemNameParser.FormatAmount(
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
            .Replace('\u2019', '\'')
            .Replace("'", string.Empty);

        normalized = LeadingQuantityWithX.Replace(normalized, string.Empty);
        normalized = LeadingQuantityWithoutX.Replace(normalized, string.Empty);
        normalized = NonAlphaNumeric.Replace(normalized, " ");
        normalized = normalized.Trim().ToUpperInvariant();
        normalized = SplitPossessive.Replace(normalized, "$1S $2");
        normalized = ItemNameParser.ApplyNormalizedWordSwaps(normalized);

        return normalized;
    }
}
