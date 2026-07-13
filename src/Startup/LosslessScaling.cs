using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Startup;

public static class LosslessScaling
{
    private static readonly TimeSpan CacheInterval = TimeSpan.FromSeconds(5);
    private static bool? _cachedIsRunning;
    private static DateTime _cachedRunningAt = DateTime.MinValue;
    private static ILogger? _logger;

    public static void SetLogger(ILogger logger) => _logger = logger;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

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
                var hwnd = FindWindow(null, "Lossless Scaling");
                _cachedIsRunning = hwnd != IntPtr.Zero;
                _logger?.LogDebug("LosslessScaling detected: {Running} (window check)", _cachedIsRunning.Value);
            }
            catch
            {
                _cachedIsRunning = false;
                _logger?.LogWarning("LosslessScaling detection failed (FindWindow error)");
            }

            return _cachedIsRunning.Value;
        }
    }
}
