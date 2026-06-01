using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Configuration;

public sealed class SettingsController(
    IConfiguration configuration,
    ILogger<SettingsController> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private DateTime _lastWriteUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settingsPath = ResolveSettingsPath();
        if (settingsPath is not null && File.Exists(settingsPath))
        {
            _lastWriteUtc = File.GetLastWriteTimeUtc(settingsPath);
        }

        RefreshConfiguration("startup");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (HasSettingsFileChanged())
                {
                    RefreshConfiguration("file-changed");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh settings from src/appsettings.json.");
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private bool HasSettingsFileChanged()
    {
        var settingsPath = ResolveSettingsPath();
        if (settingsPath is null || !File.Exists(settingsPath))
        {
            return false;
        }

        var currentWriteUtc = File.GetLastWriteTimeUtc(settingsPath);
        if (currentWriteUtc <= _lastWriteUtc)
        {
            return false;
        }

        _lastWriteUtc = currentWriteUtc;
        return true;
    }

    private void RefreshConfiguration(string reason)
    {
        if (configuration is not IConfigurationRoot root)
        {
            logger.LogWarning("Configuration root does not support explicit reload; settings polling is disabled.");
            return;
        }

        root.Reload();
        logger.LogInformation("Settings reloaded from src/appsettings.json ({Reason}).", reason);
    }

    private static string? ResolveSettingsPath()
    {
        var appBasePath = Path.Combine(AppContext.BaseDirectory, "src", "appsettings.json");
        if (File.Exists(appBasePath))
        {
            return appBasePath;
        }

        var cwdPath = Path.Combine(Environment.CurrentDirectory, "src", "appsettings.json");
        if (File.Exists(cwdPath))
        {
            return cwdPath;
        }

        return null;
    }
}
