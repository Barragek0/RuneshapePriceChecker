using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class PoeNinjaClientTests
{
    [Fact]
    public async Task FetchPrices_EmptyLines_ReturnsEmptySnapshot()
    {
        var handler = new MockHttpHandler();
        handler.AddResponse("/api/poe2/economy/exchange/current/overview?league=Standard&type=Currency",
            new { lines = Array.Empty<object>() });

        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.ExactPrices);
    }

    [Fact]
    public async Task FetchPrices_WithCurrencyLines_ParsesChaosEquivalent()
    {
        var handler = new MockHttpHandler();
        handler.AddResponse("/api/poe2/economy/exchange/current/overview?league=Standard&type=Currency",
            new
            {
                lines = new[]
                {
                    new { chaosEquivalent = 1.0, currencyTypeName = "Chaos Orb" },
                    new { chaosEquivalent = 240.0, currencyTypeName = "Divine Orb" }
                }
            });

        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);

        Assert.True(snapshot.ExactPrices.Count >= 1);
    }

    [Fact]
    public async Task FetchPrices_DifferentLeague_EncodedCorrectly()
    {
        var handler = new MockHttpHandler();
        handler.AddResponse("/api/poe2/economy/exchange/current/overview?league=Runes%20of%20Aldur&type=Currency",
            new { lines = Array.Empty<object>() });

        var httpClient = new HttpClient(handler);
        var options = Options.Create(new PricingCacheOptions
        {
            PoeNinjaBaseUrl = "https://poe.ninja",
            League = "Runes of Aldur",
            IncludedTypes = ["Currency"],
            ExchangeOverviewPath = "api/poe2/economy/exchange/current/overview",
            StashItemOverviewPath = "api/poe2/economy/stash/current/item/overview"
        });
        var logger = new LoggerFactory().CreateLogger<PoeNinjaClient>();
        var client = new PoeNinjaClient(httpClient, new NinjaOptionsMonitor<PricingCacheOptions>(options), logger);

        var snapshot = await client.FetchPricesAsync("Runes of Aldur", CancellationToken.None);
        Assert.NotNull(snapshot);
    }

    private static PoeNinjaClient CreateClient(MockHttpHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new PricingCacheOptions
        {
            PoeNinjaBaseUrl = "https://poe.ninja",
            League = "Standard",
            IncludedTypes = ["Currency"],
            ExchangeOverviewPath = "api/poe2/economy/exchange/current/overview",
            StashItemOverviewPath = "api/poe2/economy/stash/current/item/overview"
        });
        var logger = new LoggerFactory().CreateLogger<PoeNinjaClient>();
        return new PoeNinjaClient(httpClient, new NinjaOptionsMonitor<PricingCacheOptions>(options), logger);
    }
}

internal sealed class NinjaOptionsMonitor<T>(IOptions<T> options) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => options.Value;
    public T Get(string? name) => options.Value;
    public IDisposable? OnChange(Action<T, string?> listener) => null!;
}
