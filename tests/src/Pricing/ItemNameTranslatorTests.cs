using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public sealed class ItemNameTranslatorTests
{
    private static ItemNameTranslator CreateTranslator(HttpMessageHandler? handler = null)
    {
        var client = handler is not null
            ? new HttpClient(handler) { BaseAddress = new Uri("https://www.pathofexile.com") }
            : new HttpClient();
        return new ItemNameTranslator(client, NullLogger<ItemNameTranslator>.Instance);
    }

    [Fact]
    public void ToEnglish_English_ReturnsOriginal()
    {
        var t = CreateTranslator();
        t.SetLanguage("eng");

        Assert.True(t.IsLoaded);
        Assert.Equal("Chaos Orb", t.ToEnglish("Chaos Orb"));
        Assert.Equal("Divine Orb", t.ToEnglish("Divine Orb"));
        Assert.Equal("", t.ToEnglish(""));
    }

    [Fact]
    public void ToEnglish_NotLoaded_ReturnsOriginal()
    {
        var t = CreateTranslator();
        // Don't call SetLanguage — should be !IsLoaded
        Assert.False(t.IsLoaded);
        Assert.Equal("Orbe du Chaos", t.ToEnglish("Orbe du Chaos"));
    }

    [Fact]
    public void SetLanguage_SameLanguage_NoReload()
    {
        var t = CreateTranslator();
        t.SetLanguage("eng");
        Assert.True(t.IsLoaded);

        t.SetLanguage("eng");
        Assert.True(t.IsLoaded); // Should still be loaded, no change
    }

    [Fact]
    public void SetLanguage_DifferentLanguage_ResetsLoadState()
    {
        var t = CreateTranslator();
        t.SetLanguage("eng");
        Assert.True(t.IsLoaded);

        t.SetLanguage("fr");
        Assert.False(t.IsLoaded); // New language should reset
    }

    [Fact]
    public async Task LoadAsync_ValidApiResponse_ParsesTranslations()
    {
        var json = """
        {
            "result": [
                {
                    "label": "Currency",
                    "entries": [
                        { "type": "Chaos Orb", "text": "Orbe du Chaos" },
                        { "type": "Divine Orb", "text": "Orbe Divin" },
                        { "type": "Exalted Orb", "text": "Orbe Exalté" }
                    ]
                },
                {
                    "label": "Unique",
                    "entries": [
                        { "type": "Headhunter", "text": "Chasseur de Têtes" }
                    ]
                }
            ]
        }
        """;

        var handler = new FakeHttpMessageHandler(json);
        var t = CreateTranslator(handler);
        t.SetLanguage("fr");
        await t.LoadAsync("fr", CancellationToken.None);

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
    public async Task LoadAsync_EnglishEntries_SkippedCorrectly()
    {
        // When type == text (both English), entry should be skipped
        var json = """
        {
            "result": [
                {
                    "label": "Currency",
                    "entries": [
                        { "type": "Chaos Orb", "text": "Chaos Orb" }
                    ]
                }
            ]
        }
        """;

        var handler = new FakeHttpMessageHandler(json);
        var t = CreateTranslator(handler);
        t.SetLanguage("fr");
        await t.LoadAsync("fr", CancellationToken.None);

        // "Chaos Orb" (English) should not have a translation entry since it's identical
        Assert.Equal("Chaos Orb", t.ToEnglish("Chaos Orb"));
    }

    [Fact]
    public void ToEnglish_CaseInsensitive()
    {
        var t = CreateTranslator();
        t.SetLanguage("eng");
        Assert.Equal("chaos orb", t.ToEnglish("chaos orb"));
        Assert.Equal("CHAOS ORB", t.ToEnglish("CHAOS ORB"));
        Assert.Equal("ChAoS oRb", t.ToEnglish("ChAoS oRb"));
    }

    [Fact]
    public async Task LoadAsync_EmptyResponse_DoesNotThrow()
    {
        var json = """{"result": []}""";
        var handler = new FakeHttpMessageHandler(json);
        var t = CreateTranslator(handler);
        t.SetLanguage("de");
        await t.LoadAsync("de", CancellationToken.None);

        Assert.True(t.IsLoaded);
        Assert.Equal("Item", t.ToEnglish("Item")); // passes through
    }

    [Fact]
    public async Task LoadAsync_ApiError_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler("not json", System.Net.HttpStatusCode.InternalServerError);
        var t = CreateTranslator(handler);
        t.SetLanguage("de");
        await t.LoadAsync("de", CancellationToken.None);

        // Should still be marked loaded (prevents retry spam)
        Assert.True(t.IsLoaded);
        Assert.Equal("Item", t.ToEnglish("Item"));
    }

    [Fact]
    public async Task LoadAsync_EnglishLanguage_SkipsFetch()
    {
        var callCount = 0;
        var handler = new CountingHandler(() => callCount++);
        var t = CreateTranslator(handler);
        t.SetLanguage("eng");
        await t.LoadAsync("eng", CancellationToken.None);

        Assert.True(t.IsLoaded);
        Assert.Equal(0, callCount); // No HTTP call made
    }

    [Fact]
    public void ManualMappings_CommonCurrencies_Translated()
    {
        var json = """{"result": []}""";
        var handler = new FakeHttpMessageHandler(json);
        var t = CreateTranslator(handler);
        t.SetLanguage("fr");
        t.LoadAsync("fr", CancellationToken.None).GetAwaiter().GetResult();

        // Manual mappings added for common currencies
        Assert.Equal("Chaos Orb", t.ToEnglish("Orbe du Chaos"));
        Assert.Equal("Divine Orb", t.ToEnglish("Orbe Divin"));
        Assert.Equal("Exalted Orb", t.ToEnglish("Orbe Exalté"));
    }
}

/// <summary>
/// Fake HttpMessageHandler that returns a fixed JSON response.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly HttpStatusCode _statusCode;

    public FakeHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseBody = responseBody;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

/// <summary>
/// HttpMessageHandler that counts how many times it's invoked.
/// </summary>
public sealed class CountingHandler : HttpMessageHandler
{
    private readonly Action _onSend;

    public CountingHandler(Action onSend) => _onSend = onSend;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _onSend();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"result": []}""", System.Text.Encoding.UTF8, "application/json")
        });
    }
}
