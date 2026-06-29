using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Configuration;

public static class Poe2ConfigFile
{
    private const string RelativeConfigPath = @"Documents\My Games\Path of Exile 2\poe2_production_Config.ini";

    private static ILogger? _logger;
    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string? _fileText;
    private static long _lastReadTicks;
    private static FileSystemWatcher? _watcher;
    private static string? _cachedPoe2Owner;
    private static long _lastPoe2OwnerCheckTicks;
    private static readonly object _watcherLock = new();
    public static event Action? ConfigChanged;

    private static string? ResolveConfigPath()
    {
        // Don't cache the resolved path permanently — the PoE2 process
        // owner may change (e.g. user starts PoE2 as limited user after
        // the app is already running). The caller (ReadAllText) caches
        // the result for 5 seconds, and GetPoe2ProcessOwner has its own
        // 30-second WMI cache, so this re-evaluates frequently enough
        // without being expensive.

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        var profilesDir = systemDrive is not null
            ? Path.Combine(systemDrive, "Users")
            : @"C:\Users";

        // Priority 1: If PoE2 is running, use the config from whichever user
        // owns that process. This handles both single-user and limited-user
        // scenarios without scanning unrelated profiles.
        var poe2Owner = GetPoe2ProcessOwner();
        if (poe2Owner is not null)
        {
            var poe2UserPath = Path.Combine(profilesDir, poe2Owner, RelativeConfigPath);
            if (File.Exists(poe2UserPath))
            {
                _logger?.LogDebug("PoE2 config resolved to '{Path}' (PoE2 process owner: {Owner})", poe2UserPath, poe2Owner);
                return poe2UserPath;
            }
        }

        // Priority 2: Fall back to the current user's profile (PoE2 not running,
        // or process owner has no config yet — e.g. first launch).
        var currentUser = Environment.UserName;
        if (currentUser is not null)
        {
            var currentUserPath = Path.Combine(profilesDir, currentUser, RelativeConfigPath);
            if (File.Exists(currentUserPath))
            {
                _logger?.LogDebug("PoE2 config resolved to '{Path}' (current user: {User})", currentUserPath, currentUser);
                return currentUserPath;
            }
        }

        _logger?.LogTrace("No PoE2 config file found in any user profile.");
        return null;
    }

    private static string? GetPoe2ProcessOwner()
    {
        var now = Stopwatch.GetTimestamp();
        if (_cachedPoe2Owner is not null && now - _lastPoe2OwnerCheckTicks < Stopwatch.Frequency * 30)
            return _cachedPoe2Owner;

        _lastPoe2OwnerCheckTicks = now;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, Handle, ProcessId FROM Win32_Process WHERE Name LIKE '%PathOfExile%'");
            using var results = searcher.Get();
            var foundAny = false;
            foreach (var process in results)
            {
                foundAny = true;
                var procObj = (System.Management.ManagementObject)process;
                try
                {
                    using var ownerParams = procObj.InvokeMethod("GetOwner", null, null);
                    var domain = ownerParams?["Domain"] as string;
                    var username = ownerParams?["User"] as string;
                    if (domain is not null && username is not null)
                    {
                        _logger?.LogDebug("PoE2 process owner resolved via WMI: {Domain}\\{User} (PID: {Pid})",
                            domain, username, process["ProcessId"]);
                        return _cachedPoe2Owner = username;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "WMI GetOwner failed for process PID={Pid}.", process["ProcessId"]);
                }
            }
            if (!foundAny)
                _logger?.LogTrace("WMI query found no running PathOfExile process.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WMI query for PoE2 process owner failed.");
        }
        return _cachedPoe2Owner = null;
    }

    private static void InvalidateCache()
    {
        _fileText = null;
        _lastReadTicks = 0;
        _logger?.LogTrace("PoE2 config cache invalidated.");
    }

    public static void StartWatching()
    {
        lock (_watcherLock)
        {
            if (_watcher is not null)
            {
                _logger?.LogDebug("PoE2 config watcher already running.");
                return;
            }
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
            _logger?.LogDebug("PoE2 config file change detected — cache cleared (change type: {ChangeType}, file: {Name}).", e.ChangeType, e.Name);
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
        if (text is null)
        {
            _logger?.LogTrace("PoE2 config: no file text available for lookup of '{Key}'.", key);
            return null;
        }

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0) continue;
            var result = trimmed[(eqIdx + 1)..].Trim().ToString();
            _logger?.LogTrace("PoE2 config lookup: key='{Key}' value='{Value}'", key, result);
            return result;
        }
        _logger?.LogDebug("PoE2 config lookup: key='{Key}' not found in config.", key);
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
