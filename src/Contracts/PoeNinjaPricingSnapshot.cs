namespace RuneshapePriceChecker.Contracts;

public sealed record PoeNinjaPricingSnapshot(
    IReadOnlyDictionary<string, decimal> ExactPrices,
    IReadOnlyDictionary<string, (decimal MinChaos, decimal MaxChaos)> UniqueCategoryRanges,
    decimal DivineOrbChaosValue,
    decimal ExaltedOrbChaosValue,
    decimal CurrencyMinChaos = 0m,
    decimal CurrencyMaxChaos = 0m);
