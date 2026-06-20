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
    OcrLeagueWindowReader reader,
    InMemoryPricingCache pricingCache,
    PricingOverlayRenderer overlayRenderer,
    DebugOverlayService debugOverlay,
    DashboardService dashboard,
    IOptionsMonitor<PricingCacheOptions> pricingOptions,
    IOptionsMonitor<AppOptions> appOptions,
    IOptionsMonitor<OcrOptions> ocrOptions,
    ILogger<LeaguePricingWorker> logger,
    ItemNameTranslator? translator = null) : BackgroundService
{
    private static readonly TimeSpan MinOcrInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StaleRenderTimeout = TimeSpan.FromMilliseconds(180);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var latestSnapshot = new LeagueWindowSnapshot([], DateTimeOffset.UtcNow);
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
                    else
                    {
                        // Wait just until the minimum interval has elapsed, then loop to start a new scan
                        var remaining = MinOcrInterval - sinceLastOcr;
                        await Task.Delay(remaining, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }

                Debug.Assert(inFlightSnapshotTask is not null);

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

                var snapshot = hasCompletedSnapshot
                    ? latestSnapshot
                    : new LeagueWindowSnapshot([], DateTimeOffset.UtcNow);

                if (ocrOptions.CurrentValue.HideDebugOverlayWhenInterfaceNotDetected && !snapshot.InterfaceDetected)
                {
                    snapshot = new LeagueWindowSnapshot([], DateTimeOffset.UtcNow, InterfaceDetected: false);
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

                    for (var i = 0; i < snapshot.ItemNames.Count; i++)
                    {
                        var itemName = snapshot.ItemNames[i];
                        var (normalizedItemName, quantity, level) = ParseItemAndQuantity(itemName);
                        // Translate first, then append level for more specific pricing
                        var englishName = translator?.ToEnglish(normalizedItemName) ?? normalizedItemName;
                        var priceName = level > 0 ? $"{englishName} (Level {level})" : englishName;
                        var quote = pricingCache.TryGetPriceQuote(priceName, quantity);

                        if (quote is null && (IsRareUniqueItem(normalizedItemName) || IsRareUniqueItem(englishName)))
                        {
                            quote = new PriceQuote("?", pricingOptions.CurrentValue.OrangeThreshold, false);
                        }

                        quote ??= new PriceQuote("N/A", -1m, false);

                        prices[itemName] = quote;
                    }
                }
                else
                {
                    foreach (var itemName in snapshot.ItemNames)
                        prices[itemName] = new PriceQuote("...", -1m, false);
                }

                var unpricedBanner = BuildUnpriceableBanner(snapshot.ItemNames, translator);

                if (appOptions.CurrentValue.LogLevel <= LogLevel.Debug)
                    LogVerboseSnapshot(snapshot, prices, logger);

                logger.LogTrace("Worker: calling SetBannerMessage");
                debugOverlay.SetBannerMessage(unpricedBanner);
                logger.LogTrace("Worker: calling SetDebugText");

                // Build translated lines for debug overlay (purple text below each OCR line)
                string[]? translatedLines = null;
                if (translator is not null)
                {
                    translatedLines = new string[snapshot.ItemNames.Count];
                    for (var i = 0; i < snapshot.ItemNames.Count; i++)
                    {
                        var (normalizedItemName, quantity, _) = ParseItemAndQuantity(snapshot.ItemNames[i]);
                        var translated = translator.ToEnglish(normalizedItemName) ?? normalizedItemName;
                        if (string.Equals(translated, normalizedItemName, StringComparison.OrdinalIgnoreCase))
                        {
                            var isRare = IsRareUniqueItem(normalizedItemName) || IsRareUniqueItem(translated);
                            translated = isRare ? "Rare Unique Item" : translated;

                            if (string.Equals(translated, normalizedItemName, StringComparison.OrdinalIgnoreCase))
                            {
                                var normalized = InMemoryPricingCache.Normalize(normalizedItemName);
                                foreach (var candidate in ItemNameParser.BuildUniqueCategoryLookupCandidates(normalized))
                                {
                                    if (candidate.StartsWith("UNIQUE ", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var category = candidate["UNIQUE ".Length..].Trim();
                                        if (category.Length > 0)
                                            category = char.ToUpperInvariant(category[0]) + category[1..].ToLowerInvariant();
                                        translated = $"Unique {category}";
                                        break;
                                    }
                                }
                            }
                        }
                        translatedLines[i] = $"{quantity}x {translated}";
                    }
                }

                debugOverlay.SetDebugText(snapshot.ItemNames, snapshot.RowYPositions, snapshot.InterfaceDetected, statusLine: snapshot.CaptureMethod, cropBounds: snapshot.CropBounds, retryRegions: snapshot.RetryRegions, translatedLines: translatedLines);
                if (dashboard.Metrics is { } m)
                    m.DebugOverlayActive = ocrOptions.CurrentValue.DebugOverlay;
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



    private static Task<LeagueWindowSnapshot> StartSnapshotReadTask(OcrLeagueWindowReader reader, CancellationToken stoppingToken)
    {
        return Task.Run(reader.ReadSnapshot, stoppingToken);
    }

    private static (string ItemName, int Quantity, int Level) ParseItemAndQuantity(string itemName)
    {
        var parsed = ItemNameParser.ParseDetectedItem(itemName);
        return (parsed.Name, parsed.Quantity, parsed.Level);
    }

    private static bool IsRareUniqueItem(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return false;

        // English exact + fuzzy (these are hit most often)
        if (itemName.Equals("Rare Unique Item", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("Very Rare Unique Item", StringComparison.OrdinalIgnoreCase))
            return true;

        if (StrComp.IsOneCharAway(itemName, "Rare Unique Item")
            || StrComp.IsOneCharAway(itemName, "Very Rare Unique Item")
            || StrComp.IsTwoCharsAway(itemName, "Rare Unique Item")
            || StrComp.IsTwoCharsAway(itemName, "Very Rare Unique Item"))
            return true;

        // Non-English translations: [StartsWith prefix, full string for exact/fuzzy]
        foreach (var (prefix, full) in s_rareTranslations)
        {
            if (itemName.Equals(full, StringComparison.OrdinalIgnoreCase)
                || itemName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || StrComp.IsOneCharAway(itemName, full)
                || StrComp.IsTwoCharsAway(itemName, full))
                return true;
        }

        return false;
    }

    private static readonly (string Prefix, string Full)[] s_rareTranslations =
    [
        ("Редкий уникальный", "Редкий уникальный предмет"),
        ("Seltener einzigartiger", "Seltener einzigartiger Gegenstand"),
        ("Objet rare unique", "Objet rare unique"),
        ("Objeto raro único", "Objeto raro único"),
        ("Item raro único", "Item raro único"),
    ];

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

    private static string? BuildUnpriceableBanner(IReadOnlyList<string> itemNames, ItemNameTranslator? translator = null)
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

            // Also check the translated name for non-English skill/support gems
            if (translator is not null)
            {
                var english = translator.ToEnglish(normalized);
                if (!string.Equals(english, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsUnpriceableExact(english))
                        return "Some items can't be priced, new Skills\nand Supports aren't on poe.ninja";
                    if (IsUnpriceablePrefix(english))
                        return "Some items can't be priced, new Skills\nand Supports aren't on poe.ninja";
                }
            }
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
            return;

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

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Detected {Count} items with prices: {Entries}", snapshot.ItemNames.Count, string.Join(" | ", entries));
    }
}
