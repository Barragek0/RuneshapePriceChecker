using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Configuration;

public sealed class AppOptions
{
    public LogLevel LogLevel { get; set; } = LogLevel.Information;
    public bool ForceUpdateAvailable { get; set; }
    public bool AutoApplyUpdate { get; set; }
    public bool TestMode { get; set; }
    public bool BringToForeground { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool CloseWithPoE2 { get; set; }
    public bool AllOverlaysDisabled { get; set; }
    public bool PricingOverlay { get; set; } = true;
    public bool Banner { get; set; } = true;
}
