using Xunit;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;

namespace RuneshapePriceChecker.Tests.Pricing;

public class PricingCacheTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static async Task<InMemoryPricingCache> CreateCacheAsync(string mockFile)
    {
        var path = Path.Combine(RepoRoot, "tests", "mocks", mockFile);
        using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        var exactPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var uniqueRanges = new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase);
        decimal divineValue = 0m, exaltValue = 0m, currencyMin = 0m, currencyMax = 0m;

        foreach (var row in root.EnumerateArray())
        {
            if (!row.TryGetProperty("name", out var nameProp) || !row.TryGetProperty("price", out var priceProp)) continue;
            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!TryReadDecimal(priceProp, out var price) || price <= 0) continue;

            exactPrices[name] = price;
            if (string.Equals(name, "Divine Orb", StringComparison.OrdinalIgnoreCase)) divineValue = price;
            if (string.Equals(name, "Exalted Orb", StringComparison.OrdinalIgnoreCase)) exaltValue = price;
            if (currencyMin == 0m || price < currencyMin) currencyMin = price;
            if (price > currencyMax) currencyMax = price;
        }

        var snapshot = new PricingSnapshot(exactPrices, uniqueRanges, divineValue, exaltValue, currencyMin, currencyMax);
        var source = new MockPricingSource(snapshot);
        var options = new PricingCacheOptions { RedThreshold = 0.5m, OrangeThreshold = 1.0m, GreenThreshold = 5.0m, DisplayCurrency = "chaos" };
        var cache = new InMemoryPricingCache(source, new StaticOptionsMonitor<PricingCacheOptions>(options), NullLogger<InMemoryPricingCache>.Instance);
        await cache.RefreshAsync(CancellationToken.None);
        return cache;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number) { value = element.GetDecimal(); return true; }
        if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), out value)) return true;
        value = 0; return false;
    }

    [Fact]
    public async Task RefreshAsync_LoadsMockData_CacheReady()
    {
        var cache = await CreateCacheAsync("pricing-mock.json");
        Assert.True(cache.IsReady);
    }

    [Theory]
    [InlineData("Chaos Orb", "0.9c")]
    [InlineData("Divine Orb", "1d")]
    [InlineData("Exalted Orb", "<0.1c")]
    [InlineData("Vaal Orb", "<0.1c")]
    [InlineData("Mirror of Kalandra", "78.8d")]
    public async Task TryGetPriceQuote_ExactMatch(string itemName, string expectedLabel)
    {
        var cache = await CreateCacheAsync("pricing-mock.json");
        var quote = cache.TryGetPriceQuote(itemName);
        Assert.NotNull(quote);
        Assert.Equal(expectedLabel, quote!.Label);
    }

    [Theory]
    [InlineData("ZxYzNotAnItem")]
    [InlineData("Completely Fake Item Name")]
    public async Task TryGetPriceQuote_UnknownItem_ReturnsNull(string itemName)
    {
        var cache = await CreateCacheAsync("pricing-mock.json");
        var quote = cache.TryGetPriceQuote(itemName);
        Assert.Null(quote);
    }

    [Theory]
    [InlineData("chaos orb")]
    [InlineData("CHAOS ORB")]
    [InlineData("Chaos Orb")]
    public async Task TryGetPriceQuote_CaseInsensitive(string itemName)
    {
        var cache = await CreateCacheAsync("pricing-mock.json");
        var quote = cache.TryGetPriceQuote(itemName);
        Assert.NotNull(quote);
    }

    [Fact]
    public async Task TryGetPriceQuote_UncutGems_ReturnNonNull()
    {
        var cache = await CreateCacheAsync("pricing-mock.json");
        Assert.NotNull(cache.TryGetPriceQuote("Uncut Support Gem"));
    }
}
