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
    IOptionsMonitor<PricingCacheOptions> pricingOptions,
    IOptionsMonitor<AppOptions> appOptions,
    ILogger<LeaguePricingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TargetLoopInterval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var latestSnapshot = new LeagueWindowSnapshot(Array.Empty<string>(), DateTimeOffset.UtcNow);
        var hasCompletedSnapshot = false;
        Task<LeagueWindowSnapshot>? inFlightSnapshotTask = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var loopStarted = Stopwatch.GetTimestamp();

            try
            {
                inFlightSnapshotTask ??= StartSnapshotReadTask(reader, stoppingToken);

                if (inFlightSnapshotTask.IsCompleted)
                {
                    try
                    {
                        latestSnapshot = await inFlightSnapshotTask.ConfigureAwait(false);
                        hasCompletedSnapshot = true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "OCR snapshot read failed.");
                    }

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        inFlightSnapshotTask = StartSnapshotReadTask(reader, stoppingToken);
                    }
                }

                var snapshot = hasCompletedSnapshot
                    ? latestSnapshot
                    : new LeagueWindowSnapshot(Array.Empty<string>(), DateTimeOffset.UtcNow);

                var prices = new Dictionary<string, PriceQuote?>(StringComparer.OrdinalIgnoreCase);

                foreach (var itemName in snapshot.ItemNames)
                {
                    var (normalizedItemName, quantity) = ParseItemAndQuantity(itemName);
                    var quote = pricingCache.TryGetPriceQuote(normalizedItemName, quantity);

                    // The game can show this generic label with unknown internals.
                    // Render a visible orange unknown marker instead of hiding the row.
                    if (quote is null && IsRareUniqueItem(normalizedItemName))
                    {
                        quote = new PriceQuote("?", pricingOptions.CurrentValue.OrangeThresholdChaos, false);
                    }

                    prices[itemName] = quote;
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

    private static Task<LeagueWindowSnapshot> StartSnapshotReadTask(ILeagueWindowReader reader, CancellationToken stoppingToken)
    {
        return Task.Run(reader.ReadSnapshot, stoppingToken);
    }

    private static (string ItemName, int Quantity) ParseItemAndQuantity(string itemName)
    {
        var parsed = PricingTextRules.ParseDetectedItem(itemName);
        return (parsed.Name, parsed.Quantity);
    }

    private static bool IsRareUniqueItem(string itemName)
    {
        return itemName.Equals("Rare Unique Item", StringComparison.OrdinalIgnoreCase);
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
