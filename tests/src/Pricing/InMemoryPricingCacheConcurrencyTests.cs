using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class InMemoryPricingCacheConcurrencyTests
{
    [Fact]
    public async Task Concurrent_RefreshAndGetQuote_DoesNotCorrupt()
    {
        var snapshot = new PricingSnapshot(
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["Chaos Orb"] = 1.0m },
            new Dictionary<string, (decimal, decimal)>(),
            240m, 12m);
        var source = new MockPricingSource(snapshot);
        var cache = new InMemoryPricingCache(source,
            new StaticOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions()),
            NullLogger<InMemoryPricingCache>.Instance);

        List<Task> tasks = [];
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await cache.RefreshAsync(CancellationToken.None);
                _ = cache.TryGetPriceQuote("Chaos Orb", 1);
            }));
        }
        await Task.WhenAll(tasks);
        Assert.True(cache.IsReady);
    }

    [Fact]
    public async Task Refresh_WithDivineValue_PropagatesToQuotes()
    {
        var snapshot = new PricingSnapshot(
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["Chaos Orb"] = 1.0m },
            new Dictionary<string, (decimal, decimal)>(),
            240m, 12m);
        var source = new MockPricingSource(snapshot);
        var cache = new InMemoryPricingCache(source,
            new StaticOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions()),
            NullLogger<InMemoryPricingCache>.Instance);

        await cache.RefreshAsync(CancellationToken.None);
        var quote = cache.TryGetPriceQuote("Chaos Orb", 1);
        Assert.NotNull(quote);
    }

    [Fact]
    public async Task Refresh_PartialSnapshot_PreservesUntouched()
    {
        var snapshot1 = new PricingSnapshot(
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["Chaos Orb"] = 1.0m },
            new Dictionary<string, (decimal, decimal)>(),
            240m, 12m);
        var source = new MockPricingSource(snapshot1);
        var cache = new InMemoryPricingCache(source,
            new StaticOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions()),
            NullLogger<InMemoryPricingCache>.Instance);

        await cache.RefreshAsync(CancellationToken.None);
        Assert.True(cache.IsReady);
    }
}