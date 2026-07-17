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

namespace RuneshapePriceChecker.App;

public sealed class LeaguePricingWorker(
    OcrLeagueWindowReader reader,
    InMemoryPricingCache pricingCache,
    PricingOverlayRenderer overlayRenderer,
    DebugOverlayService debugOverlay,
    BannerService bannerService,
    DashboardService dashboard,
    IPoe2WindowResolutionProvider windowResolution,
    IOptionsMonitor<PricingCacheOptions> pricingOptions,
    IOptionsMonitor<AppOptions> appOptions,
    IOptionsMonitor<OcrOptions> ocrOptions,
    ILogger<LeaguePricingWorker> logger,
    ItemNameTranslator? translator = null) : BackgroundService
{
    private double TargetCycleMs => ocrOptions.CurrentValue.ScanIntervalMs;

    // Cached PoE2 window check — lightweight FindWindow replaces expensive Process.GetProcesses (2s interval).
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
            var hwnd = NativeMethods.FindWindow(null, "Path of Exile 2");
            return _cachedPoe2Running = hwnd != IntPtr.Zero;
        }
        catch
        {
            return _cachedPoe2Running = false;
        }
    }
    /// <summary>Waits until Path of Exile 2 is the active foreground window, polling with lightweight FindWindow.</summary>
    private async Task<bool> WaitForPoe2ForegroundAsync(CancellationToken ct)
    {
        dashboard.SetStatus("Waiting for PoE2 window", "amber");
        logger.LogInformation("Waiting for Path of Exile 2 to be in the foreground before starting OCR...");
        while (!ct.IsCancellationRequested)
        {
            var hwnd = OCR.NativeMethods.FindWindow(null, "Path of Exile 2");
            if (hwnd != IntPtr.Zero && windowResolution.IsPoe2WindowForeground)
            {
                dashboard.SetStatus("Scanning league panel", "green");
                logger.LogDebug("PoE2 foreground confirmed — starting OCR pipeline.");
                return true;
            }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return false;
    }

    private const double MinIntervalMs = 50;  // Never scan faster than this
    private static readonly TimeSpan StaleRenderTimeout = TimeSpan.FromMilliseconds(180);
    private string _lastSnapshotHash = string.Empty;
    private double _lastOcrDurationMs;
    private bool _poe2WasRunning; // tracks whether PoE2 was ever seen running

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("LeaguePricingWorker started");

        // Wait for PoE2 window to be in the foreground before starting OCR.
        // This eliminates unnecessary WinRT/COM/metadata operations (OCR capture,
        // WMI queries, process enumeration) when the app is sitting idle, which
        // also reduces the chance of triggering the .NET 8.0.27 MetaDataGetDispenser
        // race condition from concurrent COM interop paths.
        if (!await WaitForPoe2ForegroundAsync(stoppingToken).ConfigureAwait(false))
            return;

        var latestSnapshot = new LeagueWindowSnapshot([], DateTimeOffset.UtcNow);
        var hasCompletedSnapshot = false;
        Task<LeagueWindowSnapshot>? inFlightSnapshotTask = null;
        var lastOcrStart = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogTrace("LeaguePricingWorker loop iteration");
            var enteredGate = MetadataGate.TryEnter();
            try
            {
                if (!enteredGate)
                {
                    await Task.Delay(50, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Close with PoE2: shut down when the game exits.
                // Uses lightweight FindWindow, not Process.GetProcesses.
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

                // Pause OCR when PoE2 isn't the foreground window — don't waste
                // cycles capturing, recognizing, and resolving prices when the game
                // is in the background. This also reduces concurrent COM interop
                // that can trigger the .NET 8.0.27 MetaDataGetDispenser race.
                if (!windowResolution.IsPoe2WindowForeground)
                {
                    if (inFlightSnapshotTask is null)
                    {
                        dashboard.SetStatus("Waiting for PoE2 window", "amber");
                        await Task.Delay(200, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                    // If there's an in-flight task, let it complete below
                    // (it will return an empty snapshot since the reader
                    // also checks IsPoe2WindowForeground).
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

                var unpricedBanner = BuildUnpriceableBanner(snapshot.ItemNames, pricingOptions.CurrentValue.PricingSource, translator);

                // Check for low-volume items to build volume warning banner
                var volumeLines = new List<string>();
                if (pricingOptions.CurrentValue.TradeVolumeWarning)
                {
                    var hasLowVolume = false;
                    var hasVeryLowVolume = false;
                    foreach (var kvp in prices)
                    {
                        if (kvp.Value?.VolumeLevel == VolumeLevel.Low) hasLowVolume = true;
                        if (kvp.Value?.VolumeLevel == VolumeLevel.VeryLow) hasVeryLowVolume = true;
                        if (hasLowVolume && hasVeryLowVolume) break;
                    }

                    if (hasLowVolume && pricingOptions.CurrentValue.TradeVolumeBanner)
                    {
                        // \0RRGGBB prefix for yellow text (matches pricing overlay yellow)
                        volumeLines.Add("\0FFC436\u26A0 = Low trade volume");
                    }

                    if (hasVeryLowVolume && pricingOptions.CurrentValue.TradeVolumeBanner)
                    {
                        // Red is the default color, no prefix needed
                        volumeLines.Add("\u26A0 = Very low trade volume");
                    }
                }

                string? combinedBanner;
                if (unpricedBanner is not null && volumeLines.Count > 0)
                {
                    combinedBanner = $"{unpricedBanner}\n{string.Join("\n", volumeLines)}";
                }
                else if (volumeLines.Count > 0)
                {
                    combinedBanner = string.Join("\n", volumeLines);
                }
                else
                {
                    combinedBanner = unpricedBanner;
                }

                if (appOptions.CurrentValue.LogLevel <= LogLevel.Debug)
                    LogVerboseSnapshot(snapshot, prices, pricingOptions.CurrentValue, logger);

                logger.LogTrace("Worker: calling SetBannerMessage");
                bannerService.SetBannerMessage(combinedBanner);
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

                        var baseLine = $"{quantity}x {translated}";
                        var quote = prices.TryGetValue(snapshot.ItemNames[i], out var q) ? q : null;
                        if (quote?.CurrentQuantity is not null)
                        {
                            var volMark = quote.VolumeLevel switch
                            {
                                VolumeLevel.VeryLow => " [RED]",
                                VolumeLevel.Low => " [YELLOW]",
                                VolumeLevel.Normal => "",
                                _ => ""
                            };
                            baseLine += $" qty={quote.CurrentQuantity.Value:N0}{volMark}";
                        }
                        translatedLines[i] = baseLine;
                    }
                }

                debugOverlay.SetDebugText(snapshot.ItemNames, snapshot.RowYPositions, snapshot.InterfaceDetected, statusLine: snapshot.CaptureMethod, cropBounds: snapshot.CropBounds, retryRegions: snapshot.RetryRegions, translatedLines: translatedLines, rejectedRegions: snapshot.RejectedRegions);
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
            finally { if (enteredGate) MetadataGate.Exit(); }
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

    private static string? BuildUnpriceableBanner(IReadOnlyList<string> itemNames, string pricingSource, ItemNameTranslator? translator = null)
    {
        // Format the source name for display
        var sourceDisplay = string.Equals(pricingSource, "poe.ninja", StringComparison.OrdinalIgnoreCase)
            ? "poe.ninja"
            : "poe2scout";

        foreach (var name in itemNames)
        {
            var parsed = ItemNameParser.ParseDetectedItem(name);
            var normalized = parsed.Name.Trim();

            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (IsPricedUncut(normalized))
                continue;

            if (IsUnpriceableExact(normalized))
                return $"Some items can't be priced, New Skills / Supports aren't on {sourceDisplay}";

            if (IsUnpriceablePrefix(normalized))
                return $"Some items can't be priced, New Skills / Supports aren't on {sourceDisplay}";

            // Also check the translated name for non-English skill/support gems
            if (translator is not null)
            {
                var english = translator.ToEnglish(normalized);
                if (!string.Equals(english, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsUnpriceableExact(english))
                        return $"Some items can't be priced, New Skills / Supports aren't on {sourceDisplay}";
                    if (IsUnpriceablePrefix(english))
                        return $"Some items can't be priced, New Skills / Supports aren't on {sourceDisplay}";
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
        PricingCacheOptions pricing,
        ILogger<LeaguePricingWorker> logger)
    {
        if (snapshot.ItemNames.Count == 0)
            return;

        // Replicate auto threshold computation so the log shows the same colors the overlay renders.
        var colorOpts = pricing;
        if (pricing.AutoPriceThresholds)
        {
            var maxPrice = 0m;
            foreach (var kvp in prices)
            {
                if (kvp.Value is null || kvp.Value.IsRange) continue;
                if (kvp.Value.VolumeLevel != VolumeLevel.Normal) continue;
                if (PriceColorCalculator.TryParseDisplayedChaosEquivalent(kvp.Value.Label, pricing, out var displayValue))
                {
                    if (displayValue > maxPrice)
                        maxPrice = displayValue;
                }
                else if (kvp.Value.RepresentativeChaosValue > maxPrice)
                {
                    maxPrice = kvp.Value.RepresentativeChaosValue;
                }
            }

            if (maxPrice > 0m)
            {
                colorOpts = new PricingCacheOptions
                {
                    AutoPriceThresholds = true,
                    RedThreshold = maxPrice * 0.1m,
                    OrangeThreshold = maxPrice * 0.3m,
                    GreenThreshold = maxPrice * 0.7m,
                    DisplayCurrency = pricing.DisplayCurrency,
                    League = pricing.League,
                    PricingSource = pricing.PricingSource,
                    TradeVolumeMatchColor = pricing.TradeVolumeMatchColor,
                    TradeVolumeBanner = pricing.TradeVolumeBanner
                };
            }
        }

        var entries = new string[snapshot.ItemNames.Count];
        for (var i = 0; i < snapshot.ItemNames.Count; i++)
        {
            var itemName = snapshot.ItemNames[i];
            var quote = prices.TryGetValue(itemName, out var currentQuote) ? currentQuote : null;
            var display = quote is null ? "n/a" : quote.Label;
            var matchDetail = string.IsNullOrWhiteSpace(quote?.MatchDetail)
                ? string.Empty
                : $" ({quote.MatchDetail})";
            var volume = quote?.CurrentQuantity is not null
                ? $" qty={quote.CurrentQuantity.Value:N0}"
                : "";
            var volLevel = quote?.VolumeLevel switch
            {
                VolumeLevel.VeryLow => " [RED]",
                VolumeLevel.Low => " [YELLOW]",
                VolumeLevel.Normal => "",
                _ => ""
            };

            // Compute the actual rendered color (respecting auto thresholds and MatchColor)
            var colorInfo = "";
            if (quote is not null && !quote.IsRange && PriceColorCalculator.TryParseDisplayedChaosEquivalent(quote.Label, colorOpts, out var parsedValue))
            {
                var color = PriceColorCalculator.GetPriceColor(parsedValue, colorOpts);

                // Apply MatchColor override (same logic as BuildTextSegments in the renderers)
                if (quote.VolumeLevel != VolumeLevel.Normal && colorOpts.TradeVolumeMatchColor)
                {
                    color = quote.VolumeLevel switch
                    {
                        VolumeLevel.VeryLow => Color.FromArgb(255, 255, 72, 72),   // red
                        VolumeLevel.Low => Color.FromArgb(255, 255, 196, 54),       // yellow
                        VolumeLevel.Normal => color,
                        _ => color
                    };
                }

                colorInfo = $" RGB({color.R},{color.G},{color.B})";
            }

            entries[i] = $"{itemName}={display}{volume}{volLevel}{colorInfo}{matchDetail}";
        }

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Detected {Count} items with prices: {Entries}", snapshot.ItemNames.Count, string.Join(" | ", entries));
    }
}
