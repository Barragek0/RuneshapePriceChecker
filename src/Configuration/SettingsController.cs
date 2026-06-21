using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace RuneshapePriceChecker.Configuration;

public sealed class SettingsController(
    IConfiguration configuration,
    ILogger<SettingsController> logger) : BackgroundService
{
    private const string SettingsFileName = "appsettings.json";
    private FileSystemWatcher? _watcher;
    private DateTime _lastReloadUtc = DateTime.MinValue;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshConfiguration();

        var settingsPath = ResolveSettingsPath();
        if (settingsPath is not null && File.Exists(settingsPath))
        {
            var dir = Path.GetDirectoryName(settingsPath)!;
            _watcher = new FileSystemWatcher(dir, SettingsFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            // Debounce: OS often fires multiple Changed events per save.
            // Ignore events within 500ms of the last reload so the first
            // real edit is never skipped (unlike the old _ignoreNextChange
            // approach which discarded the first event entirely).
            _watcher.Changed += (_, _) =>
            {
                var now = DateTime.UtcNow;
                if ((now - _lastReloadUtc).TotalMilliseconds < 500)
                    return;
                _lastReloadUtc = now;
                try { RefreshConfiguration(); }
                catch (Exception ex) { logger.LogError(ex, "Failed to reload settings: {Context}", ErrorContext.FromException(ex)); }
            };
        }
        else
        {
            logger.LogWarning("Settings file not found; file watching disabled.");
        }

        _ = stoppingToken.Register(() => _watcher?.Dispose());
        return Task.CompletedTask;
    }

    private void RefreshConfiguration()
    {
        if (configuration is not IConfigurationRoot root)
        {
            logger.LogWarning("Configuration root does not support reload.");
            return;
        }

        var settingsPath = ResolveSettingsPath();
        if (settingsPath is not null && File.Exists(settingsPath))
        {
            try
            {
                var text = File.ReadAllText(settingsPath);
                _ = JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Settings file contains invalid JSON — reload skipped: {Context} ({Path})", ErrorContext.FromException(ex), settingsPath);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read settings file — reload skipped.");
                return;
            }
        }

        root.Reload();
        logger.LogInformation("Settings reloaded successfully.");
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
