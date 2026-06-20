using System.Diagnostics;
using System.Globalization;

namespace RuneshapePriceChecker.Configuration;

public static class Poe2ConfigFile
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games", "Path of Exile 2", "poe2_Production_Config.ini");

    private static string? _fileText;
    private static long _lastReadTicks;
    private static FileSystemWatcher? _watcher;
    private static readonly object _watcherLock = new();
    public static event Action? ConfigChanged;
    public static void StartWatching()
    {
        lock (_watcherLock)
        {
            if (_watcher is not null) return;
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir, "poe2_Production_Config.ini")
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnConfigFileChanged;
        }
    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: the file system fires multiple events for a single save
        Thread.Sleep(500);
        lock (_watcherLock)
        {
            // Force re-read by clearing the cache
            _fileText = null;
            _lastReadTicks = 0;
            ConfigChanged?.Invoke();
        }
    }

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
    public static double? MouseCursorSize
    {
        get
        {
            var val = GetValue("mouse_cursor_size=");
            if (val is null) return null;
            return double.TryParse(val, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
    }
    public static int? ResolutionWidth
    {
        get
        {
            var val = GetValue("resolution_width=");
            return val is not null && int.TryParse(val, out var w) ? w : null;
        }
    }
    public static int? ResolutionHeight
    {
        get
        {
            var val = GetValue("resolution_height=");
            return val is not null && int.TryParse(val, out var h) ? h : null;
        }
    }
    public static bool IsFullscreen
    {
        get
        {
            var val = GetValue("fullscreen=");
            return val is not null && val.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }
    public static float? UiBrightness
    {
        get
        {
            var val = GetValue("ui_brigthness=");
            if (val is null) return null;
            return float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : null;
        }
    }

    private static readonly Dictionary<string, string> GameToTesseractLang = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng",
        ["fr"] = "fra",
        ["de"] = "deu",
        ["es"] = "spa",
        ["pt-BR"] = "por",
        ["ru"] = "rus",
        ["th"] = "tha",
        ["zh-TW"] = "chi_tra",
        ["ko-KR"] = "kor",
        ["ja-JP"] = "jpn",
    };
    public static string? Language
    {
        get
        {
            var val = GetValue("language=");
            if (val is null) return null;
            return GameToTesseractLang.TryGetValue(val, out var code) ? code : null;
        }
    }
    public static int CursorBoxWidth => InterpolateCursor(20, 48);
    public static int CursorBoxHeight => InterpolateCursor(25, 66);

    private static int InterpolateCursor(int min, int max)
    {
        var size = MouseCursorSize ?? 2.75;
        var t = Math.Clamp((size - 1.5) / (4.0 - 1.5), 0.0, 1.0);
        return (int)Math.Round(min + t * (max - min));
    }
}
