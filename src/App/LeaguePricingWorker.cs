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
using System.Globalization;
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
    private double TargetCycleMs => ocrOptions.CurrentValue.ScanIntervalMs;

    // Cached PoE2 process check — Process.GetProcesses is expensive (2s interval).
    private static readonly TimeSpan Poe2CheckInterval = TimeSpan.FromSeconds(2);
    private static bool _cachedPoe2Running;
    private static DateTime _lastPoe2CheckAt = DateTime.MinValue;

    private static bool IsPoe2ProcessRunning()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastPoe2CheckAt) < Poe2CheckInterval)
            return _cachedPoe2Running;

        _lastPoe2CheckAt = now;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero &&
                        !string.IsNullOrWhiteSpace(p.MainWindowTitle) &&
                        p.MainWindowTitle.Equals("Path of Exile 2", StringComparison.OrdinalIgnoreCase))
                    {
                        return _cachedPoe2Running = true;
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }
            return _cachedPoe2Running = false;
        }
        catch
        {
            return _cachedPoe2Running = false;
        }
    }
    private const double MinIntervalMs = 50;  // Never scan faster than this
    private static readonly TimeSpan StaleRenderTimeout = TimeSpan.FromMilliseconds(180);
    private string _lastSnapshotHash = string.Empty;
    private double _lastOcrDurationMs;
    private bool _poe2WasRunning; // tracks whether PoE2 was ever seen running

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
                // Close with PoE2: shut down when the game exits.
                // Process.GetProcessesByName is expensive so use a 5-second cache.
                if (appOptions.CurrentValue.CloseWithPoE2)
                {
                    if (IsPoe2ProcessRunning())
                    {
                        if (!_poe2WasRunning)
                            logger.LogDebug("PoE2 process detected — CloseWithPoE2 armed.");
                        _poe2WasRunning = true;
                    }
                    else if (_poe2WasRunning)
                    {
                        logger.LogInformation("PoE2 process not found — shutting down as requested (CloseWithPoE2 enabled).");
                        // Signal the RPC service that this was CloseWithPoE2 (not manual close)
                        try
                        {
                            using var evt = new EventWaitHandle(false, EventResetMode.ManualReset,
                                RpcServiceRunner.CloseByPoe2EventName);
                            _ = evt.Set();
                        }
                        catch { }
                        Process.GetCurrentProcess().Kill();
                        return;
                    }
                }

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
                    var targetInterval = Math.Max(MinIntervalMs, TargetCycleMs - _lastOcrDurationMs);
                    if (sinceLastOcr.TotalMilliseconds >= targetInterval)
                    {
                        lastOcrStart = Stopwatch.GetTimestamp();
                        logger.LogTrace("Worker: starting OCR task");
                        inFlightSnapshotTask = StartSnapshotReadTask(reader, stoppingToken);
                    }
                    else
                    {
                        var remaining = targetInterval - sinceLastOcr.TotalMilliseconds;
                        await Task.Delay(TimeSpan.FromMilliseconds(remaining), stoppingToken).ConfigureAwait(false);
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
                        _lastOcrDurationMs = Stopwatch.GetElapsedTime(lastOcrStart).TotalMilliseconds;
                        logger.LogTrace("Worker: OCR task completed, reading snapshot");
                        latestSnapshot = await inFlightSnapshotTask.ConfigureAwait(false);
                        hasCompletedSnapshot = true;
                        logger.LogTrace("Worker: snapshot has {Count} items, interfaceDetected={Detected}", latestSnapshot.ItemNames.Count, latestSnapshot.InterfaceDetected);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "OCR snapshot read failed: {Context} (had {Count} items)", ErrorContext.FromException(ex), latestSnapshot.ItemNames.Count);
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

                if (snapshot.InterfaceDetected && debugOverlay.NeedsInitialSetup())
                {
                    logger.LogInformation("Triggering initial setup (InterfaceDetected={Detected})", snapshot.InterfaceDetected);
                    debugOverlay.RunInitialSetup();
                }

                // Skip all overlay work when snapshot content unchanged (same items, same positions)
                var snapshotHash = ComputeSnapshotHash(snapshot);
                if (snapshotHash == _lastSnapshotHash)
                {
                    // Still need to handle interface-not-detected hiding
                    if (ocrOptions.CurrentValue.HideDebugOverlayWhenInterfaceNotDetected && !snapshot.InterfaceDetected)
                        debugOverlay.ForceHide();
                    continue;
                }
                logger.LogTrace("Worker: snapshot content changed (hash {OldHash} -> {NewHash}, {Count} items, interface={Detected})",
                    _lastSnapshotHash.Length > 0 ? _lastSnapshotHash[..Math.Min(8, _lastSnapshotHash.Length)] : "(empty)",
                    snapshotHash[..Math.Min(8, snapshotHash.Length)],
                    snapshot.ItemNames.Count, snapshot.InterfaceDetected);
                _lastSnapshotHash = snapshotHash;

                if (ocrOptions.CurrentValue.HideDebugOverlayWhenInterfaceNotDetected && !snapshot.InterfaceDetected)
                {
                    snapshot = new LeagueWindowSnapshot([], DateTimeOffset.UtcNow, InterfaceDetected: false);
                    debugOverlay.ForceHide();
                }

                var prices = new Dictionary<string, PriceQuote?>(StringComparer.OrdinalIgnoreCase);
                var parsedItems = new (string ItemName, int Quantity, int Level)[snapshot.ItemNames.Count];

                if (pricingCache.IsReady)
                {
                    logger.LogTrace("Worker: resolving {Count} prices", snapshot.ItemNames.Count);

                    for (var i = 0; i < snapshot.ItemNames.Count; i++)
                    {
                        var itemName = snapshot.ItemNames[i];
                        var parsed = ParseItemAndQuantity(itemName);
                        parsedItems[i] = parsed;
                        var (normalizedItemName, quantity, level) = parsed;
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
                    for (var i = 0; i < snapshot.ItemNames.Count; i++)
                    {
                        parsedItems[i] = ParseItemAndQuantity(snapshot.ItemNames[i]);
                        prices[snapshot.ItemNames[i]] = new PriceQuote("...", -1m, false);
                    }
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
                        var (normalizedItemName, quantity, _) = parsedItems[i];
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
                logger.LogError(ex, "Failed to render overlay snapshot: {Context}", ErrorContext.FromException(ex));
            }
        }
    }



    private static Task<LeagueWindowSnapshot> StartSnapshotReadTask(OcrLeagueWindowReader reader, CancellationToken stoppingToken) =>
        Task.Run(reader.ReadSnapshot, stoppingToken);

    private static (string ItemName, int Quantity, int Level) ParseItemAndQuantity(string itemName)
    {
        var parsed = ItemNameParser.ParseDetectedItem(itemName);
        return (parsed.Name, parsed.Quantity, parsed.Level);
    }

    private static string ComputeSnapshotHash(LeagueWindowSnapshot snapshot)
    {
        // Quick hash of item names + row positions + capture method + interface state
        var hash = 17;
        foreach (var name in snapshot.ItemNames)
            hash = (hash * 31) + name.GetHashCode(StringComparison.Ordinal);
        if (snapshot.RowYPositions is not null)
            foreach (var y in snapshot.RowYPositions)
                hash = (hash * 31) + y;
        hash = (hash * 31) + (snapshot.CaptureMethod?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = (hash * 31) + (snapshot.InterfaceDetected ? 1 : 0);
        return hash.ToString(CultureInfo.InvariantCulture);
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
