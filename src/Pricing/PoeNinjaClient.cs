using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.Pricing;

public sealed class PoeNinjaClient(HttpClient httpClient, IOptionsMonitor<PricingCacheOptions> options, ILogger<PoeNinjaClient> logger) : IPoeNinjaClient
{
    private readonly IOptionsMonitor<PricingCacheOptions> _options = options;
    private static readonly Regex NonAlphaNumeric = new("[^A-Za-z0-9]+", RegexOptions.Compiled);
    private const string DivineOrbKey = "DIVINE ORB";

    public async Task<PoeNinjaPricingSnapshot> FetchCurrentPricesAsync(CancellationToken cancellationToken)
    {
        var pricingOptions = _options.CurrentValue;
        var exactPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var uniqueCategoryRanges = new Dictionary<string, (decimal MinChaos, decimal MaxChaos)>(StringComparer.OrdinalIgnoreCase);

        var encodedLeague = Uri.EscapeDataString(pricingOptions.League);
        var typesToFetch = pricingOptions.IncludedTypes
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var baseUri = new Uri(pricingOptions.PoeNinjaBaseUrl, UriKind.Absolute);

        foreach (var type in typesToFetch)
        {
            var endpoint = GetEndpointForType(type, pricingOptions);
            var encodedType = Uri.EscapeDataString(type);
            var requestPath = $"/{endpoint}?league={encodedLeague}&type={encodedType}";
            var requestUri = new Uri(baseUri, requestPath);

            using var response = await httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var parsedCount = 0;
            if (!document.RootElement.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("Poe.ninja returned no 'lines' array for type {Type}.", type);
                continue;
            }

            foreach (var line in lines.EnumerateArray())
            {
                if (!TryExtractPrice(line, out var chaosPrice))
                {
                    continue;
                }

                var keys = ExtractCandidateKeys(line);
                var addedAny = false;
                foreach (var key in keys)
                {
                    var normalized = InMemoryPricingCache.Normalize(key);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        continue;
                    }

                    exactPrices[normalized] = chaosPrice;
                    addedAny = true;
                }

                if (type.StartsWith("Unique", StringComparison.OrdinalIgnoreCase) &&
                    TryGetString(line, "category", out var category))
                {
                    var categoryKey = InMemoryPricingCache.Normalize($"Unique {category}");
                    if (!string.IsNullOrWhiteSpace(categoryKey))
                    {
                        if (uniqueCategoryRanges.TryGetValue(categoryKey, out var existingRange))
                        {
                            uniqueCategoryRanges[categoryKey] =
                                (Math.Min(existingRange.MinChaos, chaosPrice), Math.Max(existingRange.MaxChaos, chaosPrice));
                        }
                        else
                        {
                            uniqueCategoryRanges[categoryKey] = (chaosPrice, chaosPrice);
                        }
                    }
                }

                if (addedAny)
                {
                    parsedCount++;
                }
            }

            logger.LogInformation("Fetched {Count} price rows from poe.ninja type {Type}.", parsedCount, type);
        }

        var divineOrbChaosValue = exactPrices.TryGetValue(DivineOrbKey, out var divineValue) && divineValue > 0m
            ? divineValue
            : 150m;

        return new PoeNinjaPricingSnapshot(exactPrices, uniqueCategoryRanges, divineOrbChaosValue);
    }

    private static string GetEndpointForType(string type, PricingCacheOptions options)
    {
        if (type.StartsWith("Unique", StringComparison.OrdinalIgnoreCase))
        {
            return options.StashItemOverviewPath.TrimStart('/');
        }

        return options.ExchangeOverviewPath.TrimStart('/');
    }

    private static bool TryExtractPrice(JsonElement line, out decimal chaosPrice)
    {
        chaosPrice = 0m;

        if (line.TryGetProperty("primaryValue", out var primaryValueElement) &&
            TryReadDecimal(primaryValueElement, out chaosPrice) &&
            chaosPrice > 0)
        {
            return true;
        }

        if (line.TryGetProperty("chaosValue", out var chaosValueElement) &&
            TryReadDecimal(chaosValueElement, out chaosPrice) &&
            chaosPrice > 0)
        {
            return true;
        }

        if (line.TryGetProperty("chaosEquivalent", out var chaosEquivalentElement) &&
            TryReadDecimal(chaosEquivalentElement, out chaosPrice) &&
            chaosPrice > 0)
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractCandidateKeys(JsonElement line)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryGetString(line, "name", out var name))
        {
            yield return name;
            seen.Add(name);
        }

        if (TryGetString(line, "currencyTypeName", out var currencyTypeName) && seen.Add(currencyTypeName))
        {
            yield return currencyTypeName;
        }

        if (TryGetString(line, "baseType", out var baseType) && seen.Add(baseType))
        {
            yield return baseType;
        }

        if (TryGetString(line, "id", out var id))
        {
            if (seen.Add(id))
            {
                yield return id;
            }

            var expanded = ExpandSlug(id);
            if (!string.IsNullOrWhiteSpace(expanded) && seen.Add(expanded))
            {
                yield return expanded;
            }

            foreach (var alias in PricingTextRules.ExpandIdAliases(id))
            {
                if (!string.IsNullOrWhiteSpace(alias) && seen.Add(alias))
                {
                    yield return alias;
                }
            }
        }
    }

    private static bool TryGetString(JsonElement line, string propertyName, out string value)
    {
        value = string.Empty;
        if (!line.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static string ExpandSlug(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var cleaned = NonAlphaNumeric.Replace(id, " ").Trim();
        return cleaned;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text) &&
                decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0m;
        return false;
    }
}
