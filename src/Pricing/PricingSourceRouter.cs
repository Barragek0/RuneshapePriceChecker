using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.Pricing;

public sealed class PricingSourceRouter(
    IServiceProvider serviceProvider,
    IOptionsMonitor<PricingCacheOptions> options,
    ILogger<PricingSourceRouter>? logger = null) : IPricingSource
{
    private IPricingSource Current
    {
        get
        {
            var isPoe2Scout = string.Equals(options.CurrentValue.PricingSource, "poe2scout", StringComparison.OrdinalIgnoreCase);
            if (logger is not null)
                logger.LogDebug("Pricing source resolved: {Source}", isPoe2Scout ? "poe2scout" : "poe.ninja");
            return isPoe2Scout
                ? serviceProvider.GetRequiredService<Poe2ScoutClient>()
                : serviceProvider.GetRequiredService<PoeNinjaClient>();
        }
    }

    public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken cancellationToken)
    {
        logger?.LogTrace("FetchLeaguesAsync via {Source}", options.CurrentValue.PricingSource);
        return Current.FetchLeaguesAsync(cancellationToken);
    }

    public async Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken cancellationToken)
    {
        logger?.LogDebug("FetchPricesAsync: league={League}, source={Source}", league, options.CurrentValue.PricingSource);
        try
        {
            var result = await Current.FetchPricesAsync(league, cancellationToken).ConfigureAwait(false);
            logger?.LogDebug("FetchPricesAsync completed for {Source} (league={League})", options.CurrentValue.PricingSource, league);
            return result;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "FetchPricesAsync failed for {Source} (league={League}): {Context}",
                options.CurrentValue.PricingSource, league, ErrorContext.FromException(ex));
            throw;
        }
    }
}
