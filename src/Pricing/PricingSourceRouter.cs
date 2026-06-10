using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.Pricing;

public sealed class PricingSourceRouter(
    IServiceProvider serviceProvider,
    IOptionsMonitor<PricingCacheOptions> options) : IPricingSource
{
    private IPricingSource Current =>
        string.Equals(options.CurrentValue.PricingSource, "poe2scout", StringComparison.OrdinalIgnoreCase)
            ? serviceProvider.GetRequiredService<Poe2ScoutClient>()
            : serviceProvider.GetRequiredService<PoeNinjaClient>();

    public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct)
        => Current.FetchLeaguesAsync(ct);

    public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken ct)
        => Current.FetchPricesAsync(league, ct);
}
