namespace RuneshapePriceChecker.Contracts;

public sealed record PriceQuote(string Label, decimal RepresentativeChaosValue, bool IsRange);
