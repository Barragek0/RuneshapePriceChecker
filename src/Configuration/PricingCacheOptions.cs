namespace RuneshapePriceChecker.Configuration;

public sealed class PricingCacheOptions
{
    public string PoeNinjaBaseUrl { get; set; } = "https://poe.ninja";

    public string ExchangeOverviewPath { get; set; } = "/poe2/api/economy/exchange/current/overview";

    public string StashItemOverviewPath { get; set; } = "/poe2/api/economy/stash/current/item/overview";

    public string League { get; set; } = "Runes of Aldur";

    public string[] IncludedTypes { get; set; } =
    [
        "Currency",
        "Runes",
        "Verisium",
        "UniqueWeapons",
        "UniqueArmours",
        "UniqueAccessories"
    ];

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    public decimal RedThresholdChaos { get; set; } = 0.5m;

    public decimal OrangeThresholdChaos { get; set; } = 1m;

    public decimal GreenThresholdChaos { get; set; } = 5m;
}
