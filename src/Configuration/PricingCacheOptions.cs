namespace RuneshapePriceChecker.Configuration;

public sealed class PricingCacheOptions
{
    public string PricingSource { get; set; } = "poe2scout";

    public string League { get; set; } = "Runes of Aldur";

    public bool AutoPriceThresholds { get; set; } = true;
    public decimal RedThreshold { get; set; } = 0.5m;
    public decimal OrangeThreshold { get; set; } = 1m;
    public decimal GreenThreshold { get; set; } = 5m;
    public string DisplayCurrency { get; set; } = "exalt";
    public bool TradeVolumeWarning { get; set; } = true;
    public bool TradeVolumeMatchColor { get; set; } = true;
    public bool TradeVolumeBanner { get; set; } = true;
}
