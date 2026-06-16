using System.Diagnostics;
using System.Globalization;

namespace RuneshapePriceChecker.Configuration;

/// <summary>
/// Shared reader for poe2_Production_Config.ini. Values are cached and auto-refreshed
/// every 5 seconds so config changes are picked up without re-reading every call.
/// </summary>
public static class Poe2ConfigFile
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games", "Path of Exile 2", "poe2_Production_Config.ini");

    private static string? _fileText;
    private static long _lastReadTicks;

    private static string? ReadAllText()
    {
        var now = Stopwatch.GetTimestamp();
        if (_fileText is not null && now - _lastReadTicks < Stopwatch.Frequency * 5)
            return _fileText;

        _lastReadTicks = now;
        try
        {
            _fileText = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
        }
        catch
        {
            _fileText = null;
        }
        return _fileText;
    }

    private static string? GetValue(string key)
    {
        var text = ReadAllText();
        if (text is null) return null;

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0) continue;
            return trimmed[(eqIdx + 1)..].Trim().ToString();
        }
        return null;
    }

    /// <summary>mouse_cursor_size value (1.5–4.0). Returns null if not found.</summary>
    public static double? MouseCursorSize
    {
        get
        {
            var val = GetValue("mouse_cursor_size=");
            if (val is null) return null;
            return double.TryParse(val, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
    }

    /// <summary>resolution_width value. Returns null if not found.</summary>
    public static int? ResolutionWidth
    {
        get
        {
            var val = GetValue("resolution_width=");
            return val is not null && int.TryParse(val, out var w) ? w : null;
        }
    }

    /// <summary>resolution_height value. Returns null if not found.</summary>
    public static int? ResolutionHeight
    {
        get
        {
            var val = GetValue("resolution_height=");
            return val is not null && int.TryParse(val, out var h) ? h : null;
        }
    }

    /// <summary>Whether fullscreen mode is enabled.</summary>
    public static bool IsFullscreen
    {
        get
        {
            var val = GetValue("fullscreen=");
            return val is not null && val.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>ui_brigthness value. Returns null if not found.</summary>
    public static float? UiBrightness
    {
        get
        {
            var val = GetValue("ui_brigthness=");
            if (val is null) return null;
            return float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : null;
        }
    }

    /// <summary>
    /// Interpolated cursor box width in pixels. Calibrated at 1.5→20, 2.18→28, 4.0→48.
    /// </summary>
    public static int CursorBoxWidth => InterpolateCursor(20, 48);

    /// <summary>
    /// Interpolated cursor box height in pixels. Calibrated at 1.5→25, 2.18→36, 4.0→66.
    /// </summary>
    public static int CursorBoxHeight => InterpolateCursor(25, 66);

    private static int InterpolateCursor(int min, int max)
    {
        var size = MouseCursorSize ?? 2.75;
        var t = Math.Clamp((size - 1.5) / (4.0 - 1.5), 0.0, 1.0);
        return (int)Math.Round(min + t * (max - min));
    }
}
