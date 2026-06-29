namespace RuneshapePriceChecker.Contracts;

public interface IPricingSource
{
    Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken cancellationToken);

    Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken cancellationToken);
}

public sealed record PricingSnapshot(
    IReadOnlyDictionary<string, decimal> ExactPrices,
    IReadOnlyDictionary<string, (decimal MinChaos, decimal MaxChaos)> UniqueCategoryRanges,
    decimal DivineOrbChaosValue,
    decimal ExaltedOrbChaosValue,
    decimal CurrencyMinChaos = 0m,
    decimal CurrencyMaxChaos = 0m,
    IReadOnlyDictionary<string, string>? UniqueItemBaseTypes = null,
    IReadOnlyDictionary<string, int>? ItemQuantities = null);
