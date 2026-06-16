using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Pricing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App.Dashboard;

namespace RuneshapePriceChecker.App;

public sealed class PricingCacheRefreshWorker(
    IPricingCache cache,
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

        _ = _options.OnChange((updated, _) =>
        {
            var sourceChanged = !string.Equals(updated.PricingSource, _lastPricingSource, StringComparison.OrdinalIgnoreCase);
            var leagueChanged = !string.Equals(updated.League, _lastLeague, StringComparison.OrdinalIgnoreCase);
            if (!sourceChanged && !leagueChanged)
                return;

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
                await cache.RefreshAsync(stoppingToken).ConfigureAwait(false);
                ((InMemoryPricingCache)cache).SetOcrLanguage(ocrOptions.CurrentValue.Language);
                _lastPricingSource = _options.CurrentValue.PricingSource;
                _lastLeague = _options.CurrentValue.League;
                logger.LogInformation("Pricing cache refreshed at {Timestamp}", DateTimeOffset.UtcNow);

                delay = _options.CurrentValue.RefreshInterval;
                if (delay <= TimeSpan.Zero)
                    delay = TimeSpan.FromMinutes(10);
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

        refreshCts.Dispose();
    }


}
