using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public sealed class ItemNameTranslatorTests
{
#pragma warning disable CA2000 // Ownership transferred to the returned object
    private static ItemNameTranslator CreateTranslator(HttpMessageHandler? handler = null)
    {
        var logger = NullLogger<ItemNameTranslator>.Instance;
        if (handler is null)
            return new ItemNameTranslator(logger);
        var cache = CreateCache(handler);
        return new ItemNameTranslator(logger, cache);
    }

    private static TranslationCache CreateCache(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var ocrDir = Path.Combine(Path.GetTempPath(), "RPC-Tests", Guid.NewGuid().ToString());
        return new TranslationCache(client, NullLogger<TranslationCache>.Instance, ocrDir);
    }
#pragma warning restore CA2000

    [Fact]
    public void ToEnglish_English_ReturnsOriginal()
    {
        using var t = CreateTranslator();
        t.SetLanguage("eng");

        Assert.True(t.IsLoaded);
        Assert.Equal("Chaos Orb", t.ToEnglish("Chaos Orb"));
        Assert.Equal("Divine Orb", t.ToEnglish("Divine Orb"));
        Assert.Equal("", t.ToEnglish(""));
    }

    [Fact]
    public void ToEnglish_NotLoaded_ReturnsOriginal()
    {
        using var t = CreateTranslator();
        // Don't call SetLanguage — should be !IsLoaded
        Assert.False(t.IsLoaded);
        Assert.Equal("Orbe du Chaos", t.ToEnglish("Orbe du Chaos"));
    }

    [Fact]
    public void SetLanguage_SameLanguage_NoReload()
    {
        using var t = CreateTranslator();
        t.SetLanguage("eng");
        Assert.True(t.IsLoaded);

        t.SetLanguage("eng");
        Assert.True(t.IsLoaded); // Should still be loaded, no change
    }

    [Fact]
    public void SetLanguage_DifferentLanguage_ResetsLoadState()
    {
        using var t = CreateTranslator();
        t.SetLanguage("eng");
        Assert.True(t.IsLoaded);

        t.SetLanguage("fr");
        Assert.False(t.IsLoaded); // New language should reset
    }

    [Fact]
    public async Task LoadAsync_ValidNdjsonResponse_ParsesTranslations()
    {
        var frNdjson = """
{"name":"Orbe du Chaos","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"Orbe Divin","refName":"Divine Orb","namespace":"ITEM"}
{"name":"Orbe Exalté","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"Chasseur de Têtes","refName":"Headhunter","namespace":"UNIQUE"}
""";

        var ocrDir = Path.Combine(Path.GetTempPath(), "RPC-Tests", Guid.NewGuid().ToString());
#pragma warning disable CA2000 // Ownership transfers to ItemNameTranslator
        var cache = new TranslationCache(new HttpClient(), NullLogger<TranslationCache>.Instance, ocrDir);
#pragma warning restore CA2000
        cache.LoadFromString("fr", frNdjson);
        using var t = new ItemNameTranslator(NullLogger<ItemNameTranslator>.Instance, cache);
        t.SetLanguage("fr");
        await t.LoadAsync("fr", CancellationToken.None); // safe: _loadedLanguage already "fr"

        Assert.True(t.IsLoaded);
        Assert.Equal("Chaos Orb", t.ToEnglish("Orbe du Chaos"));
        Assert.Equal("Divine Orb", t.ToEnglish("Orbe Divin"));
        Assert.Equal("Exalted Orb", t.ToEnglish("Orbe Exalté"));
        Assert.Equal("Headhunter", t.ToEnglish("Chasseur de Têtes"));

        // Unknown names pass through
        Assert.Equal("Unknown Item", t.ToEnglish("Unknown Item"));

        // Empty/null pass through
        Assert.Equal("", t.ToEnglish(""));
    }

    [Fact]
    public async Task LoadAsync_IdentityEntries_Skipped()
    {
        // Identity entries (name == refName) should be skipped — no translation needed
        var ndjson = """
{"name":"Chaos Orb","refName":"Chaos Orb","namespace":"ITEM"}
""";

        var ocrDir = Path.Combine(Path.GetTempPath(), "RPC-Tests", Guid.NewGuid().ToString());
#pragma warning disable CA2000 // Ownership transfers to ItemNameTranslator
        var cache = new TranslationCache(new HttpClient(), NullLogger<TranslationCache>.Instance, ocrDir);
#pragma warning restore CA2000
        cache.LoadFromString("fr", ndjson);
        using var t = new ItemNameTranslator(NullLogger<ItemNameTranslator>.Instance, cache);
        t.SetLanguage("fr");
        await t.LoadAsync("fr", CancellationToken.None); // safe: _loadedLanguage already "fr"

        // "Chaos Orb" is identity, so no translation entry for it
        Assert.Equal("Chaos Orb", t.ToEnglish("Chaos Orb"));
    }

    [Fact]
    public void ToEnglish_CaseInsensitive()
    {
        using var t = CreateTranslator();
        t.SetLanguage("eng");
        Assert.Equal("chaos orb", t.ToEnglish("chaos orb"));
        Assert.Equal("CHAOS ORB", t.ToEnglish("CHAOS ORB"));
        Assert.Equal("ChAoS oRb", t.ToEnglish("ChAoS oRb"));
    }

    [Fact]
    public async Task LoadAsync_EmptyNdjson_LoadsButNoTranslations()
    {
        var ocrDir = Path.Combine(Path.GetTempPath(), "RPC-Tests", Guid.NewGuid().ToString());
#pragma warning disable CA2000 // Ownership transfers to ItemNameTranslator
        var cache = new TranslationCache(new HttpClient(), NullLogger<TranslationCache>.Instance, ocrDir);
#pragma warning restore CA2000
        cache.LoadFromString("de", "");
        using var t = new ItemNameTranslator(NullLogger<ItemNameTranslator>.Instance, cache);
        t.SetLanguage("de");
        await t.LoadAsync("de", CancellationToken.None);

        Assert.True(t.IsLoaded);
        Assert.Equal("Item", t.ToEnglish("Item")); // passes through
    }

    [Fact]
    public async Task LoadAsync_MissingNdjson_FallsBackToFileSystem()
    {
        // No LoadFromString — TryReadNdjson will find the real deu.ndjson on disk.
        var ocrDir = Path.Combine(Path.GetTempPath(), "RPC-Tests", Guid.NewGuid().ToString());
#pragma warning disable CA2000 // Ownership transfers to ItemNameTranslator
        var cache = new TranslationCache(new HttpClient(), NullLogger<TranslationCache>.Instance, ocrDir);
#pragma warning restore CA2000
        using var t = new ItemNameTranslator(NullLogger<ItemNameTranslator>.Instance, cache);
        t.SetLanguage("de");
        await t.LoadAsync("de", CancellationToken.None);

        // The real deu.ndjson should be found on disk and loaded
        Assert.True(t.IsLoaded);
    }

    [Fact]
    public async Task LoadAsync_EnglishLanguage_SkipsFetch()
    {
        using var t = CreateTranslator();
        t.SetLanguage("eng");
        await t.LoadAsync("eng", CancellationToken.None);

        Assert.True(t.IsLoaded);
    }

    [Fact]
    public void BundledFallback_PortalScroll_Translated()
    {
        using var t = CreateTranslator();
        t.SetLanguage("fr");
        t.LoadAsync("fr", CancellationToken.None).GetAwaiter().GetResult();

        // Portal Scroll is the only remaining item in translations.json with a French translation
        Assert.Equal("Portal Scroll", t.ToEnglish("Parchemin de Portail"));
    }
}
public sealed class FakeHttpMessageHandler(string localizedJson, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    private readonly string _localizedJson = localizedJson;
    private readonly HttpStatusCode _statusCode = statusCode;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_localizedJson, System.Text.Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}
public sealed class CountingHandler(Action onSend) : HttpMessageHandler
{
    private readonly Action _onSend = onSend;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _onSend();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"result": []}""", System.Text.Encoding.UTF8, "application/json")
        });
    }
}
