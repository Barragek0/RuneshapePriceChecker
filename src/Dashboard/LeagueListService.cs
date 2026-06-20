using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed class LeagueListService
{
    private static readonly string[] FallbackLeagues = ["Runes of Aldur", "Standard"];

    public static Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct = default)
    {
        return FetchLeaguesAsync(null, ct);
    }

    internal static async Task<IReadOnlyList<string>> FetchLeaguesAsync(HttpMessageHandler? handler, CancellationToken ct = default)
    {
        try
        {
            using var http = handler is not null ? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) } : new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RuneshapePriceChecker/1.0");

            var response = await http.GetAsync(
                "https://www.pathofexile.com/api/trade2/data/leagues",
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return FallbackLeagues;

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);

            if (!json.TryGetProperty("result", out var result))
                return FallbackLeagues;

            List<string> leagues = [];
            foreach (var league in result.EnumerateArray())
            {
                var id = league.GetProperty("id").GetString();
                if (!string.IsNullOrWhiteSpace(id) && !id.StartsWith("SSF", StringComparison.OrdinalIgnoreCase))
                    leagues.Add(id);
            }

            return leagues.Count > 0 ? leagues : FallbackLeagues;
        }
        catch
        {
            return FallbackLeagues;
        }
    }
}
