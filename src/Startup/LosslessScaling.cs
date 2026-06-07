using System.Diagnostics;

namespace RuneshapePriceChecker.Startup;

public static class LosslessScaling
{
    private static readonly TimeSpan CacheInterval = TimeSpan.FromSeconds(5);
    private static bool? _cachedIsRunning;
    private static DateTime _cachedRunningAt = DateTime.MinValue;

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
            }
            catch
            {
                _cachedIsRunning = false;
            }

            return _cachedIsRunning.Value;
        }
    }
}
