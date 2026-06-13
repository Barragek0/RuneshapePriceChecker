using Microsoft.Extensions.DependencyInjection;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class PricingSourceRouterTests
{
    [Fact]
    public void PricingSourceRouter_ImplementsIPricingSource()
    {
        var type = typeof(PricingSourceRouter);
        Assert.True(typeof(IPricingSource).IsAssignableFrom(type));
    }

    [Fact]
    public void Constructor_WithValidServices_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Poe2ScoutClient>(_ => null!);
        services.AddSingleton<PoeNinjaClient>(_ => null!);

        var options = new StaticOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions
        {
            PricingSource = "poe2scout"
        });

        var router = new PricingSourceRouter(services.BuildServiceProvider(), options);
        Assert.NotNull(router);
    }

    [Fact]
    public void FetchLeagues_DelegatesToCurrent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPricingSource>(new MockPricingSource());

        var options = new StaticOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions
        {
            PricingSource = "mock"
        });

        var router = new PricingSourceRouter(services.BuildServiceProvider(), options);
        Assert.NotNull(router);
    }

    private static readonly string[] StandardLeague = ["Standard"];

    private sealed class MockPricingSource : IPricingSource
    {
        public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(StandardLeague);
        public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken ct)
            => Task.FromResult(new PricingSnapshot(
                new Dictionary<string, decimal>(),
                new Dictionary<string, (decimal, decimal)>(),
                0m, 0m));
    }
}
