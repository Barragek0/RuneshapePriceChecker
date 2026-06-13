using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class InMemoryPricingCacheConstructionTests
{
    private sealed class MockPricingSource : IPricingSource
    {
        public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken ct) =>
            Task.FromResult(new PricingSnapshot(
                new Dictionary<string, decimal>(),
                new Dictionary<string, (decimal, decimal)>(),
                0m, 0m));
    }

    [Fact]
    public void Construct_WithMocks_DoesNotThrow()
    {
        var source = new MockPricingSource();
        var options = Options.Create(new PricingCacheOptions());
        var logger = new LoggerFactory().CreateLogger<InMemoryPricingCache>();

        var cache = new InMemoryPricingCache(source, new NinjaOptionsMonitor<PricingCacheOptions>(options), logger);
        Assert.False(cache.IsReady);
    }

    [Fact]
    public async Task RefreshAsync_WithMockSource_BecomesReady()
    {
        var source = new MockPricingSource();
        var options = Options.Create(new PricingCacheOptions());
        var logger = new LoggerFactory().CreateLogger<InMemoryPricingCache>();

        var cache = new InMemoryPricingCache(source, new NinjaOptionsMonitor<PricingCacheOptions>(options), logger);
        await cache.RefreshAsync(CancellationToken.None);

        Assert.True(cache.IsReady);
    }
}
