using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace RuneshapePriceChecker.App;

public sealed class LeaguePricingWorker(
    ILeagueWindowReader reader,
    IPricingCache pricingCache,
    IOverlayRenderer overlayRenderer,
    IOptionsMonitor<AppOptions> appOptions,
    ILogger<LeaguePricingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TargetLoopInterval = TimeSpan.FromMilliseconds(500);

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
        var parsed = PricingTextRules.ParseDetectedItem(itemName);
        return (parsed.Name, parsed.Quantity);
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
