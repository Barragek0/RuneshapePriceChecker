using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.OCR;
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
    OcrCaptureBoundsOverlayService debugOverlay,
    IOptionsMonitor<PricingCacheOptions> pricingOptions,
    IOptionsMonitor<AppOptions> appOptions,
    IOptionsMonitor<OcrOptions> ocrOptions,
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
                        logger.LogError(ex, "OCR snapshot read failed.");
                    }

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        inFlightSnapshotTask = StartSnapshotReadTask(reader, stoppingToken);
                    }
                }

                var snapshot = hasCompletedSnapshot
                    ? latestSnapshot
                    : new LeagueWindowSnapshot(Array.Empty<string>(), DateTimeOffset.UtcNow);

                if (ocrOptions.CurrentValue.HideDebugOverlayWhenInterfaceNotDetected && !snapshot.InterfaceDetected)
                {
                    snapshot = new LeagueWindowSnapshot(Array.Empty<string>(), DateTimeOffset.UtcNow, InterfaceDetected: false);
                    debugOverlay.ForceHide();
                }

                var prices = new Dictionary<string, PriceQuote?>(StringComparer.OrdinalIgnoreCase);

                foreach (var itemName in snapshot.ItemNames)
                {
                    var (normalizedItemName, quantity) = ParseItemAndQuantity(itemName);
                    var quote = pricingCache.TryGetPriceQuote(normalizedItemName, quantity);

                    if (quote is null && IsRareUniqueItem(normalizedItemName))
                    {
                        quote = new PriceQuote("?", pricingOptions.CurrentValue.OrangeThreshold, false);
                    }

                    quote ??= new PriceQuote("N/A", -1m, false);

                    prices[itemName] = quote;
                }

                var unpricedBanner = BuildUnpriceableBanner(snapshot.ItemNames);

                if (appOptions.CurrentValue.DebugLogging)
                {
                    LogVerboseSnapshot(snapshot, prices, logger);
                }

                debugOverlay.SetBannerMessage(unpricedBanner);
                debugOverlay.SetDebugText(snapshot.ItemNames, snapshot.RowYPositions, snapshot.InterfaceDetected);
                overlayRenderer.Render(snapshot, prices);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to render overlay snapshot.");
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

    private static readonly string[] UnpriceableExactNames =
    [
        "Verisium Pile"
    ];

    private static readonly string[] UnpriceablePrefixes =
    [
        "Skill ",
        "Support "
    ];

    private static readonly string[] PricedUncutPrefixes =
    [
        "Uncut Skill Gem",
        "Uncut Support Gem",
        "Uncut Spirit Gem"
    ];

    private static string? BuildUnpriceableBanner(IReadOnlyList<string> itemNames)
    {
        var found = false;
        foreach (var name in itemNames)
        {
            var parsed = PricingTextRules.ParseDetectedItem(name);
            var normalized = parsed.Name.Trim();

            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var isPricedUncut = PricedUncutPrefixes.Any(p =>
                normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (isPricedUncut)
            {
                continue;
            }

            if (UnpriceableExactNames.Any(e =>
                    normalized.Equals(e, StringComparison.OrdinalIgnoreCase)))
            {
                found = true;
                break;
            }

            if (UnpriceablePrefixes.Any(p =>
                    normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                found = true;
                break;
            }
        }

        return found
            ? "Some items can't be priced, new Skills\nand Supports aren't on poe.ninja"
            : null;
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
            var matchDetail = string.IsNullOrWhiteSpace(quote?.MatchDetail)
                ? string.Empty
                : $" ({quote.MatchDetail})";
            return $"{itemName}={display}{matchDetail}";
        });

        logger.LogDebug("Detected {Count} items with prices: {Entries}", snapshot.ItemNames.Count, string.Join(" | ", entries));
    }
}
