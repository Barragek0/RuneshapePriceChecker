using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RuneshapePriceChecker.App;

public sealed class LeaguePricingWorker(
    ILeagueWindowReader reader,
    IPricingCache pricingCache,
    IOverlayRenderer overlayRenderer,
    IOptionsMonitor<AppOptions> appOptions,
    ILogger<LeaguePricingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TargetLoopInterval = TimeSpan.FromMilliseconds(500);
    private static readonly Regex QuantityPrefix = new("^(?<quantity>\\d+|[AaIiLl|])\\s*[xX]\\s+(?<name>.+)$", RegexOptions.Compiled);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var loopStarted = Stopwatch.GetTimestamp();

            try
            {
                var snapshot = reader.ReadSnapshot();
                var prices = new Dictionary<string, PriceQuote?>(StringComparer.OrdinalIgnoreCase);

                foreach (var itemName in snapshot.ItemNames)
                {
                    var (normalizedItemName, quantity) = ParseItemAndQuantity(itemName);
                    prices[itemName] = pricingCache.TryGetPriceQuote(normalizedItemName, quantity);
                }

                if (appOptions.CurrentValue.EnableDebugLogging)
                {
                    LogVerboseSnapshot(snapshot, prices, logger);
                }

                overlayRenderer.Render(snapshot, prices);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to render overlay snapshot.");
            }

            var elapsed = Stopwatch.GetElapsedTime(loopStarted);
            var remainingDelay = TargetLoopInterval - elapsed;
            if (remainingDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static (string ItemName, int Quantity) ParseItemAndQuantity(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return (string.Empty, 1);
        }

        var match = QuantityPrefix.Match(itemName);
        if (!match.Success)
        {
            return (itemName.Trim(), 1);
        }

        var rawQuantity = match.Groups["quantity"].Value;
        var normalizedName = match.Groups["name"].Value.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return (itemName.Trim(), 1);
        }

        var quantity = rawQuantity switch
        {
            "a" or "A" or "i" or "I" or "l" or "L" or "|" => 1,
            _ when int.TryParse(rawQuantity, out var parsed) && parsed > 0 => parsed,
            _ => 1
        };

        return (normalizedName, quantity);
    }

    private static void LogVerboseSnapshot(
        LeagueWindowSnapshot snapshot,
        IReadOnlyDictionary<string, PriceQuote?> prices,
        ILogger<LeaguePricingWorker> logger)
    {
        if (snapshot.ItemNames.Count == 0)
        {
            return;
        }

        var entries = snapshot.ItemNames.Select(itemName =>
        {
            var quote = prices.TryGetValue(itemName, out var currentQuote) ? currentQuote : null;
            var display = quote is null ? "n/a" : quote.Label;
            return $"{itemName}={display}";
        });

        logger.LogInformation("Detected {Count} items with prices: {Entries}", snapshot.ItemNames.Count, string.Join(" | ", entries));
    }
}
