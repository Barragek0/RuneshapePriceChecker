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
        using var handler = new MockHttpHandler();
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
        using var handler = new MockHttpHandler();
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
        using var handler = new MockHttpHandler();
        handler.AddResponse("/api/poe2/economy/exchange/current/overview?league=Runes%20of%20Aldur&type=Currency",
            new { lines = Array.Empty<object>() });

#pragma warning disable CA2000 // HttpClient ownership transfers to PoeNinjaClient
        var httpClient = new HttpClient(handler);
#pragma warning restore CA2000
        var options = Options.Create(new PricingCacheOptions
        {
            PoeNinjaBaseUrl = "https://poe.ninja",
            League = "Runes of Aldur",
            IncludedTypes = ["Currency"],
            ExchangeOverviewPath = "api/poe2/economy/exchange/current/overview",
            StashItemOverviewPath = "api/poe2/economy/stash/current/item/overview"
        });
        using var loggerFactory = new LoggerFactory();
        var logger = loggerFactory.CreateLogger<PoeNinjaClient>();
        var client = new PoeNinjaClient(httpClient, new NinjaOptionsMonitor<PricingCacheOptions>(options), logger);

        var snapshot = await client.FetchPricesAsync("Runes of Aldur", CancellationToken.None);
        Assert.NotNull(snapshot);
    }

#pragma warning disable CA2000 // HttpClient and LoggerFactory ownership transfers to client
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
#pragma warning restore CA2000
}

internal sealed class NinjaOptionsMonitor<T>(IOptions<T> options) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => options.Value;
    public T Get(string? name)
    {
        return options.Value;
    }

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        return null!;
    }
}
