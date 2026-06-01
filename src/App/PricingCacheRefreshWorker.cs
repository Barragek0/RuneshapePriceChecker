using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.App;

public sealed class PricingCacheRefreshWorker(
    IPricingCache cache,
    IOptionsMonitor<PricingCacheOptions> options,
    ILogger<PricingCacheRefreshWorker> logger) : BackgroundService
{
    private readonly IOptionsMonitor<PricingCacheOptions> _options = options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cache.RefreshAsync(stoppingToken).ConfigureAwait(false);
                logger.LogInformation("Pricing cache refreshed at {Timestamp}", DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pricing cache refresh failed.");
            }

            var delay = _options.CurrentValue.RefreshInterval;
            if (delay <= TimeSpan.Zero)
            {
                delay = TimeSpan.FromMinutes(10);
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}
