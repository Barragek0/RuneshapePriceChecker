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
        "Expedition",
        "UncutGems",
        "Runes",
        "Verisium",
        "UniqueWeapons",
        "UniqueArmours",
        "UniqueAccessories"
    ];

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    public decimal RedThreshold { get; set; } = 0.5m;

    public decimal OrangeThreshold { get; set; } = 1m;

    public decimal GreenThreshold { get; set; } = 5m;

    // Allowed values: "exalt" or "chaos".
    public string DisplayCurrency { get; set; } = "exalt";
}
