using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Configuration;

public static class Poe2ConfigFile
{
    private const string RelativeConfigPath = @"Documents\My Games\Path of Exile 2\poe2_production_Config.ini";

    private static ILogger? _logger;
    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string? _resolvedConfigPath;
    private static string? _fileText;
    private static long _lastReadTicks;
    private static FileSystemWatcher? _watcher;
    private static readonly object _watcherLock = new();
    public static event Action? ConfigChanged;

    private static string? ResolveConfigPath()
    {
        if (_resolvedConfigPath is not null && File.Exists(_resolvedConfigPath))
            return _resolvedConfigPath;

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        var profilesDir = systemDrive is not null
            ? Path.Combine(systemDrive, "Users")
            : @"C:\Users";

        string? newest = null;
        DateTime newestWrite = DateTime.MinValue;

        try
        {
            foreach (var userDir in Directory.EnumerateDirectories(profilesDir))
            {
                var candidate = Path.Combine(userDir, RelativeConfigPath);
                try
                {
                    if (!File.Exists(candidate)) continue;

                    var writeTime = File.GetLastWriteTimeUtc(candidate);
                    if (writeTime > newestWrite)
                    {
                        newestWrite = writeTime;
                        newest = candidate;
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enumerate user profiles directory '{Dir}'.", profilesDir);
        }

        if (newest is not null)
        {
            _logger?.LogDebug("PoE2 config resolved to '{Path}' (last write: {LastWrite:O})", newest, newestWrite);
            _resolvedConfigPath = newest;
        }
        else
        {
            _logger?.LogTrace("No PoE2 config file found in any user profile.");
        }

        return _resolvedConfigPath;
    }

    private static void InvalidateCache()
    {
        _resolvedConfigPath = null;
        _fileText = null;
        _lastReadTicks = 0;
    }

    public static void StartWatching()
    {
        lock (_watcherLock)
        {
            if (_watcher is not null) return;
            var configPath = ResolveConfigPath();
            if (configPath is null)
            {
                _logger?.LogDebug("No PoE2 config file found — config change watching disabled.");
                return;
            }

            var dir = Path.GetDirectoryName(configPath);
            if (dir is null || !Directory.Exists(dir))
            {
                _logger?.LogDebug("PoE2 config directory not found at '{Dir}' — config change watching disabled.", dir);
                return;
            }

            _logger?.LogDebug("Watching PoE2 config file: {Path}", configPath);
            _watcher = new FileSystemWatcher(dir, "poe2_production_Config.ini")
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnConfigFileChanged;
        }


    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        Thread.Sleep(500);
        lock (_watcherLock)
        {
            _logger?.LogDebug("PoE2 config file change detected — cache cleared.");
            InvalidateCache();

            ConfigChanged?.Invoke();

            var latest = ResolveConfigPath();
            if (latest is not null &&
                !string.Equals(_watcher?.Path, Path.GetDirectoryName(latest), StringComparison.OrdinalIgnoreCase))
            {
                _watcher?.Dispose();
                _watcher = null;
                StartWatching();
            }
        }
    }

    private static string? ReadAllText()
    {
        var now = Stopwatch.GetTimestamp();
        if (_fileText is not null && now - _lastReadTicks < Stopwatch.Frequency * 5)
            return _fileText;

        _lastReadTicks = now;
        var configPath = ResolveConfigPath();
        if (configPath is null) return null;

        try
        {
            _fileText = File.ReadAllText(configPath);
            _logger?.LogTrace("PoE2 config read from '{Path}' ({Length} bytes).", configPath, _fileText.Length);
            return _fileText;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read PoE2 config file '{Path}': {Context}", configPath, ex.Message);
            InvalidateCache();
            return null;
        }
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
            if (val is null)
            {
                _logger?.LogTrace("PoE2 config: 'language' key not found.");
                return null;
            }
            if (GameToTesseractLang.TryGetValue(val, out var code))
            {
                _logger?.LogDebug("PoE2 game language: raw='{Raw}' tesseract='{Code}'", val, code);
                return code;
            }
            _logger?.LogDebug("PoE2 game language: raw='{Raw}' (no Tesseract mapping — falling back to configured default)", val);
            return null;
        }
    }
    public static double? MouseCursorSize
    {
        get
        {
            var val = GetValue("mouse_cursor_size=");
            if (val is null) return null;
            if (double.TryParse(val, CultureInfo.InvariantCulture, out var d))
            {
                _logger?.LogTrace("PoE2 config: mouse_cursor_size={Value}", d);
                return d;
            }
            _logger?.LogTrace("PoE2 config: mouse_cursor_size='{Raw}' (unparseable)", val);
            return null;
        }
    }
    public static bool IsFullscreen
    {
        get
        {
            var val = GetValue("fullscreen=");
            var result = val is not null && val.Equals("true", StringComparison.OrdinalIgnoreCase);
            _logger?.LogTrace("PoE2 config: fullscreen={Result} (raw='{Raw}')", result, val);
            return result;
        }
    }
    public static float? UiBrightness
    {
        get
        {
            var val = GetValue("ui_brigthness=");
            if (val is null) return null;
            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                _logger?.LogTrace("PoE2 config: ui_brightness={Value}", b);
                return b;
            }
            _logger?.LogTrace("PoE2 config: ui_brightness='{Raw}' (unparseable)", val);
            return null;
        }
    }
    public static int CursorBoxWidth => InterpolateCursor(20, 48);
    public static int CursorBoxHeight => InterpolateCursor(25, 66);

    private static int InterpolateCursor(int min, int max)
    {
        var size = MouseCursorSize ?? 2.75;
        var t = Math.Clamp((size - 1.5) / (4.0 - 1.5), 0.0, 1.0);
        return (int)Math.Round(min + (t * (max - min)));
    }
}
