using System.Net;
using System.Net.Http;
using System.Text.Json;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class LeagueListServiceTests
{
    private static TestHandler SuccessHandler()
    {
        var json = JsonSerializer.Serialize(new
        {
            result = new[]
            {
                new { id = "Standard" },
                new { id = "Hardcore" },
                new { id = "SSF Standard" },
                new { id = "  " },
                new { id = (string)null! }
            }
        });
        return new TestHandler(json, HttpStatusCode.OK);
    }

    private static TestHandler EmptyResultHandler()
    {
        var json = JsonSerializer.Serialize(new { result = Array.Empty<object>() });
        return new(json, HttpStatusCode.OK);
    }

    private static TestHandler ErrorHandler()
    {
        return new("{}", HttpStatusCode.InternalServerError);
    }

    private static TestHandler MissingResultHandler()
    {
        var json = JsonSerializer.Serialize(new { other = "data" });
        return new(json, HttpStatusCode.OK);
    }

    [Fact]
    public async Task FetchLeagues_Success_ReturnsFilteredLeagues()
    {
        using var handler = SuccessHandler();
        var leagues = await LeagueListService.FetchLeaguesAsync(handler, CancellationToken.None);
        Assert.Equal(2, leagues.Count);
        Assert.Contains("Standard", leagues);
        Assert.Contains("Hardcore", leagues);
        Assert.DoesNotContain("SSF Standard", leagues); // SSF filtered
    }

    [Fact]
    public async Task FetchLeagues_EmptyResult_ReturnsFallback()
    {
        using var handler = EmptyResultHandler();
        var leagues = await LeagueListService.FetchLeaguesAsync(handler, CancellationToken.None);
        Assert.Equal(2, leagues.Count);
        Assert.Contains("Standard", leagues);
    }

    [Fact]
    public async Task FetchLeagues_HttpError_ReturnsFallback()
    {
        using var handler = ErrorHandler();
        var leagues = await LeagueListService.FetchLeaguesAsync(handler, CancellationToken.None);
        Assert.Equal(2, leagues.Count);
    }

    [Fact]
    public async Task FetchLeagues_MissingResult_ReturnsFallback()
    {
        using var handler = MissingResultHandler();
        var leagues = await LeagueListService.FetchLeaguesAsync(handler, CancellationToken.None);
        Assert.Equal(2, leagues.Count);
    }

    [Fact]
    public async Task FetchLeagues_EmptyStringId_Filtered()
    {
        var json = JsonSerializer.Serialize(new
        {
            result = new[]
            {
                new { id = "Standard" },
                new { id = "" }
            }
        });
        using var handler = new TestHandler(json, HttpStatusCode.OK);
        var leagues = await LeagueListService.FetchLeaguesAsync(handler, CancellationToken.None);
        _ = Assert.Single(leagues);
        Assert.Equal("Standard", leagues[0]);
    }

    private sealed class TestHandler(string json, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
