using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Configuration;

public sealed class AppOptions
{
    public LogLevel LogLevel { get; set; } = LogLevel.Information;
    public bool ForceUpdateAvailable { get; set; } = false;
    public bool AutoApplyUpdate { get; set; } = false;
}
