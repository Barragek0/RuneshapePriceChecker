using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class Poe2ScoutClientErrorRecoveryTests
{
    [Fact]
    public async Task FetchPrices_EmptyItems_ReturnsEmptySnapshot()
    {
        var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new { Items = new[] { new { Value = "Standard", ShortName = "standard" } } });
        handler.AddResponse("/poe2/Leagues/standard/Currencies/ByCategory?Category=currency", new { Items = Array.Empty<object>() });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.poe2scout.com") };
        var appOptions = Options.Create(new AppOptions { LogLevel = LogLevel.Warning });
        var client = new Poe2ScoutClient(httpClient, new StaticOptionsMonitor<AppOptions>(appOptions.Value), new LoggerFactory().CreateLogger<Poe2ScoutClient>());
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);
        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task FetchLeagues_ServerError_ReturnsEmpty()
    {
        var handler = new MockHttpHandler();
        handler.AddErrorResponse("/poe2/Leagues", HttpStatusCode.InternalServerError);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.poe2scout.com") };
        var appOptions = Options.Create(new AppOptions { LogLevel = LogLevel.Warning });
        var client = new Poe2ScoutClient(httpClient, new StaticOptionsMonitor<AppOptions>(appOptions.Value), new LoggerFactory().CreateLogger<Poe2ScoutClient>());
        var leagues = await client.FetchLeaguesAsync(CancellationToken.None);
        Assert.Empty(leagues);
    }

    [Fact]
    public async Task FetchPrices_MalformedJson_ReturnsEmptySnapshot()
    {
        var handler = new TestJsonHandler("{not valid json", HttpStatusCode.OK);
        handler.AddResponse("/poe2/Leagues", new { Items = new[] { new { Value = "Standard", ShortName = "standard" } } });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.poe2scout.com") };
        var appOptions = Options.Create(new AppOptions { LogLevel = LogLevel.Warning });
        var client = new Poe2ScoutClient(httpClient, new StaticOptionsMonitor<AppOptions>(appOptions.Value), new LoggerFactory().CreateLogger<Poe2ScoutClient>());
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.ExactPrices);
    }

    [Fact]
    public async Task FetchPrices_NullTextInItem_Skipped()
    {
        var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new { Items = new[] { new { Value = "Standard", ShortName = "standard" } } });
        handler.AddResponse("/poe2/Leagues/standard/Currencies/ByCategory?Category=currency",
            new { Items = new[] { new { Text = (string?)null, CurrentPrice = 1.0 } } });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.poe2scout.com") };
        var appOptions = Options.Create(new AppOptions { LogLevel = LogLevel.Warning });
        var client = new Poe2ScoutClient(httpClient, new StaticOptionsMonitor<AppOptions>(appOptions.Value), new LoggerFactory().CreateLogger<Poe2ScoutClient>());
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.ExactPrices);
    }

    [Fact]
    public async Task FetchPrices_ZeroPriceItem_Skipped()
    {
        var handler = new MockHttpHandler();
        handler.AddResponse("/poe2/Leagues", new { Items = new[] { new { Value = "Standard", ShortName = "standard" } } });
        handler.AddResponse("/poe2/Leagues/standard/Currencies/ByCategory?Category=currency",
            new { Items = new[] { new { Text = "Chaos Orb", CurrentPrice = 0.0 } } });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.poe2scout.com") };
        var appOptions = Options.Create(new AppOptions { LogLevel = LogLevel.Warning });
        var client = new Poe2ScoutClient(httpClient, new StaticOptionsMonitor<AppOptions>(appOptions.Value), new LoggerFactory().CreateLogger<Poe2ScoutClient>());
        var snapshot = await client.FetchPricesAsync("Standard", CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.ExactPrices);
    }

    private sealed class TestJsonHandler(string rawJson, HttpStatusCode statusCode) : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _handlers = new(StringComparer.OrdinalIgnoreCase);

        public void AddResponse(string pathAndQuery, object body)
        {
            _handlers[MockHttpHandler.StripParams(pathAndQuery)] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = MockHttpHandler.StripParams(request.RequestUri!.PathAndQuery);
            if (_handlers.TryGetValue(key, out var fn))
                return Task.FromResult(fn());

            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}