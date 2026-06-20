using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public sealed class ItemNameTranslatorIntegrationTests
{
    private static InMemoryPricingCache CreateCache(
        PricingCacheOptions options,
        IPricingSource? source = null,
        ItemNameTranslator? translator = null)
    {
        source ??= new FakePricingSource();
        translator ??= CreateTranslatorWithData();
        var monitor = new FakeOptionsMonitor<PricingCacheOptions>(options);
        return new InMemoryPricingCache(source, monitor, NullLogger<InMemoryPricingCache>.Instance, translator);
    }

    private static ItemNameTranslator CreateTranslatorWithData()
    {
        var frNdjson = """
{"name":"Orbe du Chaos","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"Orbe Divin","refName":"Divine Orb","namespace":"ITEM"}
{"name":"Orbe Exalté","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"Miroir de Kalandra","refName":"Mirror of Kalandra","namespace":"ITEM"}
{"name":"Chasseur de Têtes","refName":"Headhunter","namespace":"UNIQUE"}
{"name":"Sang-mage","refName":"Mageblood","namespace":"UNIQUE"}
{"name":"Gemme de Compétence Brute","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Gemme de Soutien Brute","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Gemme d'Esprit Brute","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

        var ocrDir = Path.Combine(Path.GetTempPath(), "RPC-Tests", Guid.NewGuid().ToString());
        var cache = new TranslationCache(new HttpClient(), NullLogger<TranslationCache>.Instance, ocrDir);
        cache.LoadFromString("fr", frNdjson);
        var translator = new ItemNameTranslator(NullLogger<ItemNameTranslator>.Instance, cache);
        translator.SetLanguage("fr");
        translator.LoadAsync("fr", CancellationToken.None).GetAwaiter().GetResult();
        return translator;
    }

    [Fact]
    public async Task TranslatedItemName_MatchesPrice_ReturnsCorrectQuote()
    {
        var cache = CreateCache(new PricingCacheOptions { League = "Test" });
        await cache.RefreshAsync(CancellationToken.None);

        // French item name "Orbe du Chaos" → translated to "Chaos Orb" → matches price
        var quote = cache.TryGetPriceQuote("Orbe du Chaos");
        Assert.NotNull(quote);
        Assert.True(quote.RepresentativeChaosValue > 0);
    }

    [Fact]
    public async Task UntranslatedItemName_FallsThrough_Normally()
    {
        var cache = CreateCache(new PricingCacheOptions { League = "Test" });
        await cache.RefreshAsync(CancellationToken.None);

        // English name should work directly
        var quote = cache.TryGetPriceQuote("Chaos Orb");
        Assert.NotNull(quote);
        Assert.True(quote.RepresentativeChaosValue > 0);
    }

    [Fact]
    public async Task UnknownFrenchName_NoTranslation_ReturnsNull()
    {
        var cache = CreateCache(new PricingCacheOptions { League = "Test" });
        await cache.RefreshAsync(CancellationToken.None);

        // French name not in dictionary → translated as-is → won't match any price
        var quote = cache.TryGetPriceQuote("Objet Inconnu");
        Assert.Null(quote);
    }

    [Fact]
    public async Task MultipleFrenchItems_AllTranslateCorrectly()
    {
        var cache = CreateCache(new PricingCacheOptions { League = "Test" });
        await cache.RefreshAsync(CancellationToken.None);

        var items = new[] { "Orbe du Chaos", "Orbe Divin", "Orbe Exalté" };
        foreach (var item in items)
        {
            var quote = cache.TryGetPriceQuote(item);
            Assert.NotNull(quote);
            Assert.True(quote.RepresentativeChaosValue > 0,
                $"Item '{item}' should have a positive chaos value");
        }
    }

    [Fact]
    public async Task FrenchGemNames_TranslateAndMatch()
    {
        var cache = CreateCache(new PricingCacheOptions { League = "Test" });
        await cache.RefreshAsync(CancellationToken.None);

        var frenchGems = new[]
        {
            "Gemme de Compétence Brute",
            "Gemme de Soutien Brute",
            "Gemme d'Esprit Brute"
        };

        foreach (var gem in frenchGems)
        {
            var quote = cache.TryGetPriceQuote(gem);
            Assert.NotNull(quote);
            Assert.True(quote.RepresentativeChaosValue > 0,
                $"Gem '{gem}' should have a positive chaos value");
        }
    }

    [Fact]
    public async Task TranslatorNotLoaded_EnglishWorks_FrenchDoesNot()
    {
        // Create cache with NO translator
        var source = new FakePricingSource();
        var monitor = new FakeOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions { League = "Test" });
        var cache = new InMemoryPricingCache(source, monitor, NullLogger<InMemoryPricingCache>.Instance, translator: null);
        await cache.RefreshAsync(CancellationToken.None);

        // English should work
        Assert.NotNull(cache.TryGetPriceQuote("Chaos Orb"));

        // French without translator should NOT work
        Assert.Null(cache.TryGetPriceQuote("Orbe du Chaos"));
    }

    [Fact]
    public async Task SetOcrLanguage_English_NoFetch_TranslationsNotLoaded()
    {
        // No TranslationCache = no HTTP calls for English
        var translator = new ItemNameTranslator(NullLogger<ItemNameTranslator>.Instance);
        translator.SetLanguage("eng");

        var cache = CreateCache(new PricingCacheOptions { League = "Test" }, translator: translator);
        cache.SetOcrLanguage("eng");
        await cache.RefreshAsync(CancellationToken.None);

        // English works directly (no translation needed)
        Assert.NotNull(cache.TryGetPriceQuote("Chaos Orb"));
    }

    [Fact]
    public async Task QuantityPreserved_AfterTranslation()
    {
        var cache = CreateCache(new PricingCacheOptions { League = "Test" });
        await cache.RefreshAsync(CancellationToken.None);

        // 5x "Orbe du Chaos" → 5x "Chaos Orb" price
        var single = cache.TryGetPriceQuote("Orbe du Chaos", 1);
        var five = cache.TryGetPriceQuote("Orbe du Chaos", 5);

        Assert.NotNull(single);
        Assert.NotNull(five);
        Assert.Equal(single.RepresentativeChaosValue * 5, five.RepresentativeChaosValue);
    }
}
public sealed class FakePricingSource : IPricingSource
{
    public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<string>>(["Test League"]);
    }

    public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["Chaos Orb"] = 1m,
            ["Divine Orb"] = 150m,
            ["Exalted Orb"] = 80m,
            ["Mirror of Kalandra"] = 50000m,
            ["Headhunter"] = 200m,
            ["Mageblood"] = 350m,
            ["Uncut Skill Gem"] = 2m,
            ["Uncut Support Gem"] = 1.5m,
            ["Uncut Spirit Gem"] = 5m
        };

        return Task.FromResult(new PricingSnapshot(
            prices,
            new Dictionary<string, (decimal, decimal)>(),
            150m,
            80m));
    }
}

public sealed class FakeOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class, new()
{
    public T CurrentValue { get; } = value;
    public T Get(string? name)
    {
        return CurrentValue;
    }

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        return null;
    }
}
