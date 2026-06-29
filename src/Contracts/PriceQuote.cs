namespace RuneshapePriceChecker.Contracts;

public sealed record PriceQuote(string Label, decimal RepresentativeChaosValue, bool IsRange, string? MatchDetail = null)
{
    public VolumeLevel VolumeLevel { get; init; } = VolumeLevel.Normal;
    public int? CurrentQuantity { get; init; }
}
