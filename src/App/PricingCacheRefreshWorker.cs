using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Pricing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App.Dashboard;

namespace RuneshapePriceChecker.App;

public sealed class PricingCacheRefreshWorker(
    InMemoryPricingCache cache,
    IOptionsMonitor<PricingCacheOptions> options,
    IOptionsMonitor<OcrOptions> ocrOptions,
    ILogger<PricingCacheRefreshWorker> logger,
    DashboardService dashboard) : BackgroundService
{
    private readonly IOptionsMonitor<PricingCacheOptions> _options = options;
    private string? _lastPricingSource;
    private string? _lastLeague;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var refreshCts = new CancellationTokenSource();

        // When the PoE2 config file language changes, reload translations immediately
        // so OCR-detected items don't return N/A while waiting for the next refresh cycle.
        Poe2ConfigFile.ConfigChanged += OnGameConfigChanged;

        _ = _options.OnChange((updated, _) =>
        {
            var sourceChanged = !string.Equals(updated.PricingSource, _lastPricingSource, StringComparison.OrdinalIgnoreCase);
            var leagueChanged = !string.Equals(updated.League, _lastLeague, StringComparison.OrdinalIgnoreCase);
            if (!sourceChanged && !leagueChanged)
                return;

            logger.LogDebug("Pricing config changed: source={Source} (changed={SrcChg}) league={League} (changed={LgChg})",
                updated.PricingSource, sourceChanged, updated.League, leagueChanged);
            _lastPricingSource = updated.PricingSource;
            _lastLeague = updated.League;
            try { refreshCts.Cancel(); }
            catch (ObjectDisposedException) { }
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;

            try
            {
                logger.LogDebug("Pricing cache refresh starting (source={Source}, league={League})...",
                    _options.CurrentValue.PricingSource, _options.CurrentValue.League);
                await cache.RefreshAsync(stoppingToken).ConfigureAwait(false);
                // Use the actual game language (from PoE2 config)
                var gameLang = Poe2ConfigFile.Language ?? ocrOptions.CurrentValue.Language;
                cache.SetOcrLanguage(gameLang);
                _lastPricingSource = _options.CurrentValue.PricingSource;
                _lastLeague = _options.CurrentValue.League;
                logger.LogInformation("Pricing cache refreshed at {Timestamp} (lang={Lang})", DateTimeOffset.UtcNow, gameLang);

                delay = TimeSpan.FromMinutes(15);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pricing cache refresh failed — retrying in 5s.");
                dashboard.SetStatus("Failed to fetch prices", "red");
                delay = TimeSpan.FromSeconds(5);
            }

            if (refreshCts.IsCancellationRequested)
            {
                var old = refreshCts;
                refreshCts = new CancellationTokenSource();
                old.Dispose();
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, refreshCts.Token);
            try { await Task.Delay(delay, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
        }

        Poe2ConfigFile.ConfigChanged -= OnGameConfigChanged;
        refreshCts.Dispose();
    }

    private void OnGameConfigChanged()
    {
        try
        {
            var gameLang = Poe2ConfigFile.Language ?? ocrOptions.CurrentValue.Language;
            if (string.IsNullOrEmpty(gameLang)) return;
            cache.SetOcrLanguage(gameLang);
            logger.LogInformation("Translations reloaded for game language change to '{Lang}'", gameLang);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reload translations after game language change.");
        }
    }
}
