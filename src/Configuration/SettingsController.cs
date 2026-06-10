using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Configuration;

public sealed class SettingsController(
    IConfiguration configuration,
    ILogger<SettingsController> logger) : BackgroundService
{
    private const string SettingsFileName = "appsettings.json";
    private FileSystemWatcher? _watcher;
    private bool _ignoreNextChange = true;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshConfiguration("startup");

        var settingsPath = ResolveSettingsPath();
        if (settingsPath is not null && File.Exists(settingsPath))
        {
            var dir = Path.GetDirectoryName(settingsPath)!;
            _watcher = new FileSystemWatcher(dir, SettingsFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (_, _) =>
            {
                if (_ignoreNextChange)
                {
                    _ignoreNextChange = false;
                    return;
                }
                try { RefreshConfiguration("file-changed"); }
                catch (Exception ex) { logger.LogError(ex, "Failed to reload settings."); }
            };
        }
        else
        {
            logger.LogWarning("Settings file not found; file watching disabled.");
        }

        stoppingToken.Register(() => _watcher?.Dispose());
        return Task.CompletedTask;
    }

    private void RefreshConfiguration(string reason)
    {
        if (configuration is not IConfigurationRoot root)
        {
            logger.LogWarning("Configuration root does not support reload.");
            return;
        }

        root.Reload();
        logger.LogInformation("Settings reloaded from {SettingsFile} ({Reason}).", SettingsFileName, reason);
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
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

        return null;
    }
}
