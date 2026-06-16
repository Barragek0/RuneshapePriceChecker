using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;

namespace RuneshapePriceChecker.Pricing;

public sealed class Poe2ScoutClient(HttpClient httpClient, IOptionsMonitor<AppOptions> appOptions, ILogger<Poe2ScoutClient> logger) : IPricingSource
{
    private const string BaseUrl = "https://api.poe2scout.com/poe2";

    private string? _leagueShortName;
    private string? _lastLeague;

    public async Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{BaseUrl}/Leagues";
            var items = await FetchJsonArrayAsync(url, cancellationToken).ConfigureAwait(false);
            var leagues = new List<string>();
            foreach (var item in items)
            {
                var fullName = GetString(item, "Value");
                if (!string.IsNullOrWhiteSpace(fullName))
                    leagues.Add(fullName);
            }
            return leagues;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch POE2Scout leagues.");
            return [];
        }
    }

    private async Task<string> ResolveShortLeagueAsync(string fullName, CancellationToken ct)
    {
        if (string.Equals(_lastLeague, fullName, StringComparison.OrdinalIgnoreCase) && _leagueShortName is not null)
            return _leagueShortName;

        try
        {
            var items = await FetchJsonArrayAsync($"{BaseUrl}/Leagues", ct).ConfigureAwait(false);
            foreach (var item in items)
            {
                var value = GetString(item, "Value");
                if (string.Equals(value, fullName, StringComparison.OrdinalIgnoreCase))
                {
                    _leagueShortName = GetString(item, "ShortName") ?? NormalizeLeagueName(fullName);
                    _lastLeague = fullName;
                    return _leagueShortName;
                }
            }
        }
        catch { }

        _leagueShortName = NormalizeLeagueName(fullName);
        _lastLeague = fullName;
        return _leagueShortName;
    }

    public async Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken cancellationToken)
    {
        var exactPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var uniqueCategoryRanges = new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase);
        decimal divineValue = 0m, exaltValue = 0m;
        decimal currencyMinChaos = 0m, currencyMaxChaos = 0m;

        var shortLeague = await ResolveShortLeagueAsync(league, cancellationToken).ConfigureAwait(false);

        var categories = new[] { "currency", "expedition", "uncutgems", "runes", "verisium" };
        foreach (var cat in categories)
        {
            try
            {
                var url = $"{BaseUrl}/Leagues/{shortLeague}/Currencies/ByCategory?Category={cat}&ReferenceCurrency=chaos&SmoothingDays={PricingConstants.Poe2ScoutSmoothingDays}";
                var items = await FetchAllPagesAsync(url, cancellationToken).ConfigureAwait(false);
                foreach (var item in items)
                {
                    var name = GetString(item, "Text");
                    var chaos = GetDecimal(item, "CurrentPrice");
                    if (string.IsNullOrWhiteSpace(name) || chaos <= 0) continue;

                    exactPrices[name] = chaos;

                    if (string.Equals(cat, "currency", StringComparison.OrdinalIgnoreCase))
                    {
                        if (currencyMinChaos == 0m || chaos < currencyMinChaos) currencyMinChaos = chaos;
                        if (chaos > currencyMaxChaos) currencyMaxChaos = chaos;
                    }

                    if (string.Equals(name, "Divine Orb", StringComparison.OrdinalIgnoreCase) && divineValue == 0)
                        divineValue = chaos;
                    if (string.Equals(name, "Exalted Orb", StringComparison.OrdinalIgnoreCase) && exaltValue == 0)
                        exaltValue = chaos;
                }
                if (appOptions.CurrentValue.LogLevel <= LogLevel.Debug)
                {
                    var sampleNames = items.Take(5).Select(i => GetString(i, "Text") ?? "?").ToList();
                    var sampleValues = items.Take(5).Select(i => GetDecimal(i, "CurrentPrice")).ToList();
                    var firstKeys = items.Count > 0
                        ? string.Join(", ", items[0].EnumerateObject().Select(p => p.Name))
                        : "-";
                    logger.LogDebug("Fetched {Count} price rows from POE2Scout type {Category}. Sample: {Samples} Values: {Values} | Keys: {Keys}", items.Count, cat, string.Join(", ", sampleNames), string.Join(", ", sampleValues), firstKeys);
                }
                else
                {
                    logger.LogInformation("Fetched {Count} price rows from POE2Scout type {Category}.", items.Count, cat);
                }
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("POE2Scout: HTTP {Status} fetching {Category} (league={League})", (int?)ex.StatusCode, cat, shortLeague);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "POE2Scout: failed to parse {Category}", cat);
            }
        }

        var uniqueCategories = new[] { "weapon", "armour", "accessory" };
        var uniqueItemBaseTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in uniqueCategories)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                try
                {
                    var url = $"{BaseUrl}/Leagues/{shortLeague}/Uniques/ByCategory?Category={cat}&ReferenceCurrency=chaos&SmoothingDays={PricingConstants.Poe2ScoutSmoothingDays}";
                    var items = await FetchAllPagesAsync(url, cancellationToken).ConfigureAwait(false);
                    foreach (var item in items)
                    {
                        var name = GetString(item, "Text");
                        var chaos = GetDecimal(item, "CurrentPrice");
                        if (string.IsNullOrWhiteSpace(name) || chaos <= 0) continue;

                        var normName = InMemoryPricingCache.Normalize(name);
                        if (!string.IsNullOrWhiteSpace(normName))
                        {
                            uniqueCategoryRanges[normName] = (chaos, chaos);
                            var baseType = GetString(item, "Type");
                            if (!string.IsNullOrWhiteSpace(baseType))
                                uniqueItemBaseTypes[normName] = baseType;
                        }
                    }
                    if (appOptions.CurrentValue.LogLevel <= LogLevel.Debug)
                    {
                        var sampleNames = items.Take(5).Select(i => GetString(i, "Text") ?? "?").ToList();
                        var firstKeys = items.Count > 0
                            ? string.Join(", ", items[0].EnumerateObject().Select(p => p.Name))
                            : "-";
                        logger.LogDebug("Fetched {Count} price rows from POE2Scout type Unique/{Category}. Sample: {Samples} | Keys: {Keys}", items.Count, cat, string.Join(", ", sampleNames), firstKeys);
                    }
                    else
                    {
                        logger.LogInformation("Fetched {Count} price rows from POE2Scout type Unique/{Category}.", items.Count, cat);
                    }
                    break;
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning("POE2Scout: HTTP {Status} fetching Unique/{Category} (league={League}), attempt {Attempt}/3", (int?)ex.StatusCode, cat, shortLeague, attempt + 1);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "POE2Scout: failed to parse Unique/{Category}, attempt {Attempt}/3", cat, attempt + 1);
                    break;
                }
            }
        }

        return new PricingSnapshot(exactPrices, uniqueCategoryRanges, divineValue, exaltValue, currencyMinChaos, currencyMaxChaos, uniqueItemBaseTypes);
    }

    private static string NormalizeLeagueName(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0].ToLowerInvariant() : fullName.ToLowerInvariant();
    }

    private async Task<List<JsonElement>> FetchJsonArrayAsync(string url, CancellationToken ct)
    {
        var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        var itemsProp = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items
            : root;

        var list = new List<JsonElement>();
        foreach (var item in itemsProp.EnumerateArray())
            list.Add(item.Clone());
        return list;
    }

    private async Task<List<JsonElement>> FetchAllPagesAsync(string baseUrl, CancellationToken ct)
    {
        const int perPage = 200;
        var allItems = new List<JsonElement>();
        var page = 1;

        while (true)
        {
            var separator = baseUrl.Contains('?') ? "&" : "?";
            var url = $"{baseUrl}{separator}Page={page}&PerPage={perPage}";
            var pageItems = await FetchJsonArrayAsync(url, ct).ConfigureAwait(false);

            allItems.AddRange(pageItems);

            if (pageItems.Count < perPage)
                break;

            page++;
        }

        return allItems;
    }

    private static string? GetString(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static decimal GetDecimal(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
    }
}
