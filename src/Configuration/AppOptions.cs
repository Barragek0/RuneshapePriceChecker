namespace RuneshapePriceChecker.Configuration;

public sealed class AppOptions
{
    public bool DebugLogging { get; set; } = false;
    public string? ForceWindowSize { get; set; }
    public bool ForceUpdateAvailable { get; set; } = false;
    public bool AutoApplyUpdate { get; set; } = false;
}
