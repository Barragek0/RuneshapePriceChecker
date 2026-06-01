namespace RuneshapePriceChecker.Contracts;

public sealed record PoeNinjaPricingSnapshot(
    IReadOnlyDictionary<string, decimal> ExactPrices,
    IReadOnlyDictionary<string, (decimal MinChaos, decimal MaxChaos)> UniqueCategoryRanges,
    decimal DivineOrbChaosValue);
