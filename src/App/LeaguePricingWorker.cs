using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Overlay;
using RuneshapePriceChecker.Pricing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using StructLinq;

namespace RuneshapePriceChecker.App;

public sealed class LeaguePricingWorker(
    ILeagueWindowReader reader,
    IPricingCache pricingCache,
    IOverlayRenderer overlayRenderer,
    DebugOverlayService debugOverlay,
    DashboardService dashboard,
    IOptionsMonitor<PricingCacheOptions> pricingOptions,
    IOptionsMonitor<AppOptions> appOptions,
    IOptionsMonitor<OcrOptions> ocrOptions,
    IPoe2WindowResolutionProvider windowResolutionProvider,
    ILogger<LeaguePricingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MinOcrInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StaleRenderTimeout = TimeSpan.FromMilliseconds(180);
    private string[] _previousItemNames = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var latestSnapshot = new LeagueWindowSnapshot(Array.Empty<string>(), DateTimeOffset.UtcNow);
        var hasCompletedSnapshot = false;
        Task<LeagueWindowSnapshot>? inFlightSnapshotTask = null;
        var lastOcrStart = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (debugOverlay.IsSetupInProgress)
                {
                    dashboard.SetStatus("Initial setup — configure overlay position", "amber");
                    await Task.Delay(200, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (debugOverlay.NeedsInitialSetup())
                {
                    logger.LogInformation("Waiting for changelog to be dismissed before initial setup...");
                    await DashboardService.WaitForChangelogDismissedAsync(stoppingToken).ConfigureAwait(false);
                    logger.LogInformation("Triggering initial setup");
                    debugOverlay.RunInitialSetup();
                }

                if (inFlightSnapshotTask is null)
                {
                    var sinceLastOcr = Stopwatch.GetElapsedTime(lastOcrStart);
                    if (sinceLastOcr >= MinOcrInterval)
                    {
                        lastOcrStart = Stopwatch.GetTimestamp();
                        logger.LogTrace("Worker: starting OCR task");
                        inFlightSnapshotTask = StartSnapshotReadTask(reader, stoppingToken);
                    }
                }

                if (inFlightSnapshotTask is not null)
                {
                    var completed = await Task.WhenAny(
                        inFlightSnapshotTask,
                        Task.Delay(StaleRenderTimeout, stoppingToken)).ConfigureAwait(false);

                    if (completed == inFlightSnapshotTask)
                    {
                        try
                        {
                            logger.LogTrace("Worker: OCR task completed, reading snapshot");
                            latestSnapshot = await inFlightSnapshotTask.ConfigureAwait(false);
                            hasCompletedSnapshot = true;
                            logger.LogTrace("Worker: snapshot has {Count} items, interfaceDetected={Detected}", latestSnapshot.ItemNames.Count, latestSnapshot.InterfaceDetected);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "OCR snapshot read failed.");
                        }

                        inFlightSnapshotTask = null;
                    }
                    else
                    {
                        logger.LogTrace("Worker: OCR task timed out, waiting for completion");
                        try
                        {
                            latestSnapshot = await inFlightSnapshotTask.ConfigureAwait(false);
                            hasCompletedSnapshot = true;
                            logger.LogTrace("Worker: late snapshot has {Count} items", latestSnapshot.ItemNames.Count);
                        }
                        catch (Exception ex) { logger.LogWarning(ex, "OCR task threw after timeout."); }
                        inFlightSnapshotTask = null;
                    }
                }
                else
                {
                    await Task.Delay(20, stoppingToken).ConfigureAwait(false);
                }

                var snapshot = hasCompletedSnapshot
                    ? latestSnapshot
                    : new LeagueWindowSnapshot(Array.Empty<string>(), DateTimeOffset.UtcNow);

                if (ocrOptions.CurrentValue.HideDebugOverlayWhenInterfaceNotDetected && !snapshot.InterfaceDetected)
                {
                    snapshot = new LeagueWindowSnapshot(Array.Empty<string>(), DateTimeOffset.UtcNow, InterfaceDetected: false);
                    debugOverlay.ForceHide();
                }

                if (snapshot.InterfaceDetected && debugOverlay.NeedsInitialSetup())
                {
                    logger.LogInformation("Triggering initial setup (InterfaceDetected={Detected})", snapshot.InterfaceDetected);
                    debugOverlay.RunInitialSetup();
                }

                var prices = new Dictionary<string, PriceQuote?>(StringComparer.OrdinalIgnoreCase);

                if (pricingCache.IsReady)
                {
                    logger.LogTrace("Worker: resolving {Count} prices", snapshot.ItemNames.Count);

                    var currentItems = snapshot.ItemNames;
                    var cursorRows = GetRowsUnderCursor(snapshot);
                    debugOverlay.SetCursorIndicator(null, CursorBoxWidth, CursorBoxHeight);

                    for (var i = 0; i < currentItems.Count; i++)
                    {
                        var itemName = currentItems[i];
                        var (normalizedItemName, quantity) = ParseItemAndQuantity(itemName);
                        var quote = pricingCache.TryGetPriceQuote(normalizedItemName, quantity);

                        // Cursor artifact recovery: if the cursor is over this row and the current
                        // reading didn't price (null or negative), use the previous frame's price.
                        if ((quote is null || quote.RepresentativeChaosValue < 0)
                            && cursorRows.Contains(i)
                            && i < _previousItemNames.Length)
                        {
                            var (prevName, prevQty) = ParseItemAndQuantity(_previousItemNames[i]);
                            if (prevName.Length > 0 && prevName != normalizedItemName)
                            {
                                var prevQuote = pricingCache.TryGetPriceQuote(prevName, prevQty);
                                if (prevQuote?.RepresentativeChaosValue > 0)
                                {
                                    quote = prevQuote with
                                    {
                                        Label = prevQuote.Label + " ?",
                                        MatchDetail = $"(cursor fix: {prevName})"
                                    };
                                }
                            }
                        }

                        if (quote is null && IsRareUniqueItem(normalizedItemName))
                        {
                            quote = new PriceQuote("?", pricingOptions.CurrentValue.OrangeThreshold, false);
                        }

                        quote ??= new PriceQuote("N/A", -1m, false);

                        prices[itemName] = quote;
                    }

                    _previousItemNames = [.. currentItems];
                }
                else
                {
                    foreach (var itemName in snapshot.ItemNames)
                        prices[itemName] = new PriceQuote("...", -1m, false);
                }

                var unpricedBanner = BuildUnpriceableBanner(snapshot.ItemNames);

                if (appOptions.CurrentValue.LogLevel <= LogLevel.Debug)
                {
                    LogVerboseSnapshot(snapshot, prices, logger);
                }

                logger.LogTrace("Worker: calling SetBannerMessage");
                debugOverlay.SetBannerMessage(unpricedBanner);
                logger.LogTrace("Worker: calling SetDebugText");
                debugOverlay.SetDebugText(snapshot.ItemNames, snapshot.RowYPositions, snapshot.InterfaceDetected, statusLine: snapshot.CaptureMethod, cropBounds: snapshot.CropBounds);
                logger.LogTrace("Worker: calling Render");
                overlayRenderer.Render(snapshot, prices);
                logger.LogTrace("Worker: Render complete");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to render overlay snapshot.");
            }
        }
    }



    private static Task<LeagueWindowSnapshot> StartSnapshotReadTask(ILeagueWindowReader reader, CancellationToken stoppingToken)
    {
        return Task.Run(reader.ReadSnapshot, stoppingToken);
    }

    private static (string ItemName, int Quantity) ParseItemAndQuantity(string itemName)
    {
        var parsed = ItemNameParser.ParseDetectedItem(itemName);
        return (parsed.Name, parsed.Quantity);
    }

    private static int CursorBoxWidth => Poe2ConfigFile.CursorBoxWidth;
    private static int CursorBoxHeight => Poe2ConfigFile.CursorBoxHeight;

    private HashSet<int> GetRowsUnderCursor(LeagueWindowSnapshot snapshot)
    {
        var region = windowResolutionProvider.CurrentCaptureRegion;
        var ctx = windowResolutionProvider.CurrentWindowCaptureContext;
        if (region is null || ctx is null || snapshot.RowYPositions is not { Count: > 0 } rowYs)
            return [];

        var cursorPos = System.Windows.Forms.Cursor.Position;
        var relX = cursorPos.X - ctx.ClientX - region.X;
        var relY = cursorPos.Y - ctx.ClientY - region.Y;

        var boxRight = relX + CursorBoxWidth;
        var boxBottom = relY + CursorBoxHeight;

        if (boxRight < 0 || relX > region.Width || boxBottom < 0 || relY > region.Height)
            return [];

        var result = new HashSet<int>();
        for (var i = 0; i < rowYs.Count; i++)
        {
            var rowTop = rowYs[i] - 4;
            var rowBottom = (i + 1 < rowYs.Count) ? rowYs[i + 1] + 4 : region.Height;
            if (boxBottom >= rowTop && relY <= rowBottom)
                result.Add(i);
        }
        return result;
    }

    private static bool IsRareUniqueItem(string itemName)
    {
        return itemName.Equals("Rare Unique Item", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("Very Rare Unique Item", StringComparison.OrdinalIgnoreCase)
            || StrComp.IsOneCharAway(itemName, "Rare Unique Item")
            || StrComp.IsOneCharAway(itemName, "Very Rare Unique Item")
            || StrComp.IsTwoCharsAway(itemName, "Rare Unique Item")
            || StrComp.IsTwoCharsAway(itemName, "Very Rare Unique Item");
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
        foreach (var name in itemNames)
        {
            var parsed = ItemNameParser.ParseDetectedItem(name);
            var normalized = parsed.Name.Trim();

            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (IsPricedUncut(normalized))
                continue;

            if (IsUnpriceableExact(normalized))
                return "Some items can't be priced, new Skills\nand Supports aren't on poe.ninja";

            if (IsUnpriceablePrefix(normalized))
                return "Some items can't be priced, new Skills\nand Supports aren't on poe.ninja";
        }

        return null;
    }

    private static bool IsPricedUncut(string normalized)
    {
        foreach (var p in PricedUncutPrefixes)
        {
            if (normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return true;
            if (normalized.Length <= p.Length + 1 && StrComp.IsOneCharAway(normalized, p))
                return true;
            if (StrComp.IsTwoCharsAway(normalized, p))
                return true;
        }
        return false;
    }

    private static bool IsUnpriceableExact(string normalized)
    {
        foreach (var e in UnpriceableExactNames)
        {
            if (normalized.Equals(e, StringComparison.OrdinalIgnoreCase))
                return true;
            if (StrComp.IsOneCharAway(normalized, e))
                return true;
            if (StrComp.IsTwoCharsAway(normalized, e))
                return true;
        }
        return false;
    }

    private static bool IsUnpriceablePrefix(string normalized)
    {
        foreach (var p in UnpriceablePrefixes)
        {
            if (normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return true;
            if (StrComp.IsOneCharAway(normalized, p))
                return true;
            if (StrComp.IsTwoCharsAway(normalized, p))
                return true;
        }
        return false;
    }

    private static void LogVerboseSnapshot(
        LeagueWindowSnapshot snapshot,
        Dictionary<string, PriceQuote?> prices,
        ILogger<LeaguePricingWorker> logger)
    {
        if (snapshot.ItemNames.Count == 0)
        {
            return;
        }

        var entries = snapshot.ItemNames
            .ToStructEnumerable()
            .Select(itemName =>
            {
                var quote = prices.TryGetValue(itemName, out var currentQuote) ? currentQuote : null;
                var display = quote is null ? "n/a" : quote.Label;
                var matchDetail = string.IsNullOrWhiteSpace(quote?.MatchDetail)
                    ? string.Empty
                    : $" ({quote.MatchDetail})";
                return $"{itemName}={display}{matchDetail}";
            })
            .ToArray();

        if (logger.IsEnabled(LogLevel.Trace))
            logger.LogTrace("Detected {Count} items with prices: {Entries}", snapshot.ItemNames.Count, string.Join(" | ", entries));
    }
}
