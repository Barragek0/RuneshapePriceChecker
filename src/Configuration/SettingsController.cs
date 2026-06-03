using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Configuration;

public sealed class SettingsController(
    IConfiguration configuration,
    ILogger<SettingsController> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const string SettingsFileName = "appsettings.json";
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
                logger.LogWarning(ex, "Failed to refresh settings from {SettingsFile}.", SettingsFileName);
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
        logger.LogInformation("Settings reloaded from {SettingsFile} ({Reason}).", SettingsFileName, reason);
    }

    private static string? ResolveSettingsPath()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config", SettingsFileName);
        if (File.Exists(configPath))
        {
            return configPath;
        }

        var appBasePath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        if (File.Exists(appBasePath))
        {
            return appBasePath;
        }

        var cwdPath = Path.Combine(Environment.CurrentDirectory, SettingsFileName);
        if (File.Exists(cwdPath))
        {
            return cwdPath;
        }

        var legacyAppBasePath = Path.Combine(AppContext.BaseDirectory, "src", SettingsFileName);
        if (File.Exists(legacyAppBasePath))
        {
            return legacyAppBasePath;
        }

        var legacyCwdPath = Path.Combine(Environment.CurrentDirectory, "src", SettingsFileName);
        if (File.Exists(legacyCwdPath))
        {
            return legacyCwdPath;
        }

        return null;
    }
}
