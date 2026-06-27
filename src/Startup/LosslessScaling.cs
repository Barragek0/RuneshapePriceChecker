using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Startup;

public static class LosslessScaling
{
    private static readonly TimeSpan CacheInterval = TimeSpan.FromSeconds(5);
    private static bool? _cachedIsRunning;
    private static DateTime _cachedRunningAt = DateTime.MinValue;
    private static ILogger? _logger;

    public static void SetLogger(ILogger logger) => _logger = logger;

    public static bool IsRunning
    {
        get
        {
            var now = DateTime.UtcNow;
            if (_cachedIsRunning.HasValue && (now - _cachedRunningAt) < CacheInterval)
                return _cachedIsRunning.Value;

            _cachedRunningAt = now;
            try
            {
                var processes = Process.GetProcessesByName("LosslessScaling");
                _cachedIsRunning = processes.Length > 0;
                foreach (var p in processes) p.Dispose();
                _logger?.LogDebug("LosslessScaling detected: {Running} (process check)", _cachedIsRunning.Value);
            }
            catch
            {
                _cachedIsRunning = false;
                _logger?.LogWarning("LosslessScaling detection failed (process enumeration error)");
            }

            return _cachedIsRunning.Value;
        }
    }
}
