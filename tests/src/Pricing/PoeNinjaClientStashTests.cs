using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class PoeNinjaClientStashTests
{
    [Fact]
    public async Task FetchPrices_WithStashEndpoint_DoesNotThrow()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/api/poe2/economy/stash/current/item/overview?league=Standard&type=UniqueAccessories",
            new { lines = Array.Empty<object>() });

#pragma warning disable CA2000 // HttpClient ownership transfers to PoeNinjaClient
        var httpClient = new HttpClient(handler);
#pragma warning restore CA2000
        var options = Options.Create(new PricingCacheOptions
        {
            PoeNinjaBaseUrl = "https://poe.ninja",
            League = "Standard",
            IncludedTypes = ["UniqueAccessories"],
            StashItemOverviewPath = "api/poe2/economy/stash/current/item/overview"
        });
        using var loggerFactory = new LoggerFactory();
        var logger = loggerFactory.CreateLogger<PoeNinjaClient>();
        var client = new PoeNinjaClient(httpClient, new NinjaOptionsMonitor<PricingCacheOptions>(options), logger);

        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);
        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task FetchPrices_ServerError_ReturnsEmptySnapshot()
    {
        using var handler = new MockHttpHandler();
        handler.AddErrorResponse("/api/poe2/economy/stash/current/item/overview?league=Standard&type=UniqueAccessories",
            HttpStatusCode.InternalServerError);

#pragma warning disable CA2000 // HttpClient ownership transfers to PoeNinjaClient
        var httpClient = new HttpClient(handler);
#pragma warning restore CA2000
        var options = Options.Create(new PricingCacheOptions
        {
            PoeNinjaBaseUrl = "https://poe.ninja",
            League = "Standard",
            IncludedTypes = ["UniqueAccessories"],
            StashItemOverviewPath = "api/poe2/economy/stash/current/item/overview"
        });
        using var loggerFactory = new LoggerFactory();
        var logger = loggerFactory.CreateLogger<PoeNinjaClient>();
        var client = new PoeNinjaClient(httpClient, new NinjaOptionsMonitor<PricingCacheOptions>(options), logger);

        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.ExactPrices);
    }

    private sealed class NinjaOptionsMonitor<T>(IOptions<T> options) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => options.Value;
        public T Get(string? name)
        {
            return options.Value;
        }

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            return null;
        }
    }
}
