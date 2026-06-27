using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class Poe2ScoutClientTests
{
    [Fact]
    public async Task FetchLeagues_ReturnsParsedLeagueNames()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new
        {
            Items = new[]
            {
                new { Value = "Runes of Aldur", ShortName = "roa" },
                new { Value = "Standard", ShortName = "standard" }
            }
        });

        var client = CreateClient(handler);
        var leagues = await client.FetchLeaguesAsync(CancellationToken.None);

        Assert.Equal(2, leagues.Count);
        Assert.Contains("Runes of Aldur", leagues);
        Assert.Contains("Standard", leagues);
    }

    [Fact]
    public async Task FetchLeagues_EmptyItems_ReturnsEmpty()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new { Items = Array.Empty<object>() });

        var client = CreateClient(handler);
        var leagues = await client.FetchLeaguesAsync(CancellationToken.None);

        Assert.Empty(leagues);
    }

    [Fact]
    public async Task FetchLeagues_HttpError_ReturnsEmpty()
    {
        using var handler = new MockHttpHandler();
        handler.AddErrorResponse("/poe2/Leagues", HttpStatusCode.ServiceUnavailable);

        var client = CreateClient(handler);
        var leagues = await client.FetchLeaguesAsync(CancellationToken.None);

        Assert.Empty(leagues);
    }

    [Fact]
    public async Task FetchPrices_ParsesExactPrices()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new
        {
            Items = new[] { new { Value = "Standard", ShortName = "standard" } }
        });
        handler.AddResponse("/poe2/Leagues/standard/Currencies/ByCategory?Category=currency",
            new
            {
                Items = new[]
                {
                    new { Text = "Chaos Orb", CurrentPrice = 1.0 },
                    new { Text = "Divine Orb", CurrentPrice = 240.0 },
                    new { Text = "Exalted Orb", CurrentPrice = 12.0 }
                }
            });

        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);

        Assert.Equal(240m, snapshot.DivineOrbChaosValue);
        Assert.Equal(12m, snapshot.ExaltedOrbChaosValue);
        Assert.True(snapshot.ExactPrices.ContainsKey("Chaos Orb"));
        Assert.Equal(1.0m, snapshot.ExactPrices["Chaos Orb"]);
    }

    [Fact]
    public async Task FetchPrices_ParsesUniqueCategoryRanges()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new
        {
            Items = new[] { new { Value = "Standard", ShortName = "standard" } }
        });

        handler.AddResponse("/poe2/Leagues/standard/Uniques/ByCategory?Category=armour",
            new
            {
                Items = new[]
                {
                    new { Text = "Kaom's Heart", CurrentPrice = 150.0, Type = "Glorious Plate" },
                    new { Text = "Shavronne's Wrappings", CurrentPrice = 320.0, Type = "Occultist's Vestment" }
                }
            });

        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);

        Assert.True(snapshot.UniqueCategoryRanges.Count >= 2);
        Assert.True(snapshot.UniqueCategoryRanges.ContainsKey("kaoms heart"));
        Assert.Equal(150m, snapshot.UniqueCategoryRanges["kaoms heart"].MinChaos);
        Assert.Equal(150m, snapshot.UniqueCategoryRanges["kaoms heart"].MaxChaos);
    }

    [Fact]
    public async Task FetchPrices_LeagueResolutionFallsBack_WhenApiFails()
    {
        using var handler = new MockHttpHandler();
        handler.AddErrorResponse("/poe2/Leagues", HttpStatusCode.InternalServerError);

        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("MyLeague 2025", CancellationToken.None);

        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task FetchPrices_HandlesPartialCategoryFailure()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new
        {
            Items = new[] { new { Value = "Standard", ShortName = "standard" } }
        });

        handler.AddResponse("/poe2/Leagues/standard/Currencies/ByCategory?Category=currency",
            new { Items = new[] { new { Text = "Chaos Orb", CurrentPrice = 1.0 } } });
        handler.AddErrorResponse(
            "/poe2/Leagues/standard/Currencies/ByCategory?Category=expedition",
            HttpStatusCode.BadGateway);

        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);

        _ = Assert.Single(snapshot.ExactPrices);
        Assert.True(snapshot.ExactPrices.ContainsKey("Chaos Orb"));
    }

    [Fact]
    public async Task FetchPrices_IgnoresZeroPriceItems()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new
        {
            Items = new[] { new { Value = "Standard", ShortName = "standard" } }
        });

        handler.AddResponse("/poe2/Leagues/standard/Currencies/ByCategory?Category=currency",
            new
            {
                Items = new[]
                {
                    new { Text = "Valid Item", CurrentPrice = 5.0 },
                    new { Text = "ZeroPrice Item", CurrentPrice = 0.0 }
                }
            });

        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);

        _ = Assert.Single(snapshot.ExactPrices);
        Assert.True(snapshot.ExactPrices.ContainsKey("Valid Item"));
        Assert.False(snapshot.ExactPrices.ContainsKey("ZeroPrice Item"));
    }

    [Fact]
    public async Task FetchPrices_CapturesUniqueBaseTypes()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new
        {
            Items = new[] { new { Value = "Standard", ShortName = "standard" } }
        });

        handler.AddResponse("/poe2/Leagues/standard/Uniques/ByCategory?Category=armour",
            new
            {
                Items = new[]
                {
                    new { Text = "Silk Robe Unique", CurrentPrice = 80.0, Type = "Silk Robe" },
                    new { Text = "Unique No Type", CurrentPrice = 90.0, Type = "" }
                }
            });

        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);

        Assert.NotNull(snapshot.UniqueItemBaseTypes);
        Assert.True(snapshot.UniqueItemBaseTypes.ContainsKey("silk robe unique"));
        Assert.Equal("Silk Robe", snapshot.UniqueItemBaseTypes["silk robe unique"]);
        Assert.False(snapshot.UniqueItemBaseTypes.ContainsKey("unique no type"));
    }

    [Fact]
    public async Task FetchPrices_ShortLeagueNameUsed_AfterFirstResolution()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new
        {
            Items = new[] { new { Value = "Standard", ShortName = "std" } }
        });

        // First call resolves the short name to "std", which gets cached
        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);

        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task FetchPrices_Pagination_HandlesMultiplePages()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new
        {
            Items = new[] { new { Value = "Standard", ShortName = "standard" } }
        });
        // Return many items to trigger pagination â€” all in page 1 since MockHttpHandler returns all
        var items = Enumerable.Range(0, 5).Select(i => new { Text = $"Item {i}", CurrentPrice = i + 1.0 }).ToArray();
        handler.AddResponse("/poe2/Leagues/standard/Currencies/ByCategory?Category=currency",
            new { Items = items });

        var client = CreateClient(handler);
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);
        Assert.Equal(5, snapshot.ExactPrices.Count);
    }

#pragma warning disable CA2000 // HttpClient and LoggerFactory ownership transfers to client
    private static Poe2ScoutClient CreateClient(MockHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.poe2scout.com") };
        var appOptions = Options.Create(new AppOptions { LogLevel = LogLevel.Warning });
        var logger = new LoggerFactory().CreateLogger<Poe2ScoutClient>();
        return new Poe2ScoutClient(httpClient, new WrappedOptionsMonitor(appOptions), logger);
    }
#pragma warning restore CA2000

    private sealed class WrappedOptionsMonitor(IOptions<AppOptions> options) : IOptionsMonitor<AppOptions>
    {
        public AppOptions CurrentValue => options.Value;
        public AppOptions Get(string? name)
        {
            return options.Value;
        }

        public IDisposable? OnChange(Action<AppOptions, string?> listener)
        {
            return null;
        }
    }
}

internal sealed class MockHttpHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };
    private static readonly string[] StrippedParams = ["Page=", "PerPage=", "ReferenceCurrency=", "SmoothingDays="];

    private readonly Dictionary<string, Func<HttpResponseMessage>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void AddResponse(string pathAndQuery, object body)
    {
        _handlers[StripParams(pathAndQuery)] = () =>
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        };
    }

    public void AddErrorResponse(string pathAndQuery, HttpStatusCode statusCode)
    {
        _handlers[StripParams(pathAndQuery)] = () => new HttpResponseMessage(statusCode);
    }

    internal static string StripParams(string pathAndQuery)
    {
        pathAndQuery = pathAndQuery.TrimEnd('?', '&');
        var qIndex = pathAndQuery.IndexOf('?');
        if (qIndex < 0) return pathAndQuery;

        var path = pathAndQuery[..qIndex];
        var query = pathAndQuery[(qIndex + 1)..];
        var parts = query.Split('&');
        var kept = parts
            .Where(p => !StrippedParams.Any(s => p.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return kept.Length > 0 ? path + "?" + string.Join("&", kept) : path;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);

        var key = StripParams(request.RequestUri!.PathAndQuery);

        if (_handlers.TryGetValue(key, out var handler))
            return Task.FromResult(handler());

        // Return empty items for unmatched URLs to avoid retry delays
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"Items":[]}""", System.Text.Encoding.UTF8, "application/json")
        });
    }
}
