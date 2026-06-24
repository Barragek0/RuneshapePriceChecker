using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;

namespace RuneshapePriceChecker.App.Dashboard;

[SuppressMessage("Globalization", "CA1507", Justification = "JSON keys use different naming convention than C# properties")]
public sealed class DashboardViewModel(string configPath)
{
    private readonly string _configPath = configPath;

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = [];

    public bool LogLevelChanged { get; private set; }

    public string CurrentLeague { get; set; } = "Runes of Aldur";
    public string PricingSource { get; set; } = "poe2scout";
    public string DisplayCurrency { get; set; } = "exalt";
    public decimal RedThreshold { get; set; } = 0.5m;
    public decimal OrangeThreshold { get; set; } = 1.0m;
    public decimal GreenThreshold { get; set; } = 5.0m;
    public string LogLevel { get; set; } = "Information";
    public bool AutoPriceThresholds { get; set; } = true;
    public bool DebugOverlay { get; set; }
    public bool HideDebugOverlayWhenInterfaceNotDetected { get; set; }
    public bool SaveDebugImages { get; set; }
    public bool PricingOverlay { get; set; } = true;
    public bool Banner { get; set; } = true;
    public string OcrLanguage { get; set; } = "eng";
    public string OcrBackend { get; set; } = "windows";
    public bool AutoUpdate { get; set; } = true;
    public bool BringToForeground { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool RememberDebugPanel { get; set; }
    public bool CloseWithPoE2 { get; set; }
    public bool OpenWithPoE2 { get; set; }
    public string CaptureMode { get; set; } = "printwindow";
    public int ScanIntervalMs { get; set; } = 100;

    public Action<IProgress<int>>? OnUpdateTriggered { get; set; }
    public Action? OnSetupContinue { get; set; }
    public Action? OnReRunSetup { get; set; }

    public void OnLogEntry(LogEntry entry)
    {
        var brush = entry.Color switch
        {
            "red" => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
            "yellow" => new SolidColorBrush(Color.FromRgb(0xE8, 0xC5, 0x47)),
            "white" => new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF1)),
            _ => new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
        };

        for (int i = 0; i < LogEntries.Count; i++)
        {
            if (string.Equals(LogEntries[i].RawMessage, entry.Message, StringComparison.Ordinal))
            {
                var existing = LogEntries[i];
                existing.Count = entry.Count;
                existing.Timestamp = entry.Timestamp;
                existing.ForegroundBrush = brush;
                existing.UpdateDisplayText();
                if (i != 0)
                    LogEntries.Move(i, 0);
                return;
            }
        }

        var vm = new LogEntryViewModel
        {
            RawMessage = entry.Message,
            Timestamp = entry.Timestamp,
            Count = entry.Count,
            ForegroundBrush = brush,
            LogLevel = entry.LogLevel
        };
        vm.SetInitialText();
        LogEntries.Insert(0, vm);

        while (LogEntries.Count > 1000)
            LogEntries.RemoveAt(LogEntries.Count - 1);
    }

    public void LoadSettings()
    {
        if (!File.Exists(_configPath)) return;

        try
        {
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            if (root is null) return;

            if (root["App"] is JsonNode app)
            {
                LogLevel = app.Str("LogLevel", "Information");
                BringToForeground = app.Val("BringToForeground", true);
                AlwaysOnTop = app.Val("AlwaysOnTop", false);
                RememberDebugPanel = app.Val("RememberDebugPanel", false);
                CloseWithPoE2 = app.Val("CloseWithPoE2", false);
                OpenWithPoE2 = app.Val("OpenWithPoE2", false);
            }

            if (root["Pricing"] is JsonNode pricing)
            {
                AutoPriceThresholds = pricing.Val("AutoPriceThresholds", true);
                CurrentLeague = pricing.Str("League", "Runes of Aldur");
                PricingSource = pricing.Str("PricingSource", "poe2scout");
                DisplayCurrency = pricing.Str("DisplayCurrency", "exalt");
                RedThreshold = pricing.Val("RedThreshold", 0.5m);
                OrangeThreshold = pricing.Val("OrangeThreshold", 1.0m);
                GreenThreshold = pricing.Val("GreenThreshold", 5.0m);
            }

            if (root["OCR"] is JsonNode ocr)
            {
                DebugOverlay = ocr.Val("DebugOverlay", false);
                HideDebugOverlayWhenInterfaceNotDetected = ocr.Val("HideDebugOverlayWhenInterfaceNotDetected", false);
                SaveDebugImages = ocr.Val("SaveDebugImages", false);
                OcrLanguage = ocr.Str("Language", "eng");
                OcrBackend = ocr.Str("OcrBackend", "windows");
                CaptureMode = ocr.Str("CaptureMode", "printwindow");
                ScanIntervalMs = ocr.Val("ScanIntervalMs", 100);
            }

            if (root["App"] is JsonNode appSettings)
            {
                PricingOverlay = appSettings.Val("PricingOverlay", true);
                Banner = appSettings.Val("Banner", true);
            }

            if (root["Update"] is JsonNode update)
                AutoUpdate = update.Val("AutoUpdate", true);

            if (root["Window"] is JsonNode win)
                _ = win; // Window section kept for layout settings
        }
        catch { }
    }

    public string? SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(CurrentLeague))
            return "Select a league.";

        if (!(RedThreshold < OrangeThreshold && OrangeThreshold < GreenThreshold))
            return "Thresholds must be: Red < Orange < Green.";

        try
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (configDir is null) return null;
            _ = Directory.CreateDirectory(configDir);

            var existingJson = File.Exists(_configPath) ? File.ReadAllText(_configPath, Encoding.UTF8) : "{}";
            var root = JsonNode.Parse(existingJson) ?? new JsonObject();
            if (root is not JsonObject rootObj) return null;

            var previousLevel = rootObj["App"]?.Str("LogLevel", "Information") ?? "Information";
            LogLevelChanged = !string.Equals(previousLevel, LogLevel, StringComparison.OrdinalIgnoreCase);

            rootObj["App"] ??= new JsonObject();
            rootObj["Pricing"] ??= new JsonObject();
            rootObj["OCR"] ??= new JsonObject();
            rootObj["Update"] ??= new JsonObject();

            JsonObject? app = rootObj["App"] as JsonObject;
            if (app is not null)
            {
                app["LogLevel"] = LogLevel;
                app["BringToForeground"] = BringToForeground;
                app["AlwaysOnTop"] = AlwaysOnTop;
                app["RememberDebugPanel"] = RememberDebugPanel;
                app["CloseWithPoE2"] = CloseWithPoE2;
                app["OpenWithPoE2"] = OpenWithPoE2;
                app["PricingOverlay"] = PricingOverlay;
                app["Banner"] = Banner;
            }

            // Migrate: remove old OCR keys if they exist
            if (rootObj["OCR"] is JsonObject ocrObj)
            {
                _ = ocrObj.Remove("ShowPricingOverlay");
                _ = ocrObj.Remove("ShowBanner");
            }

            if (rootObj["Pricing"] is JsonObject pricing)
            {
                pricing["AutoPriceThresholds"] = AutoPriceThresholds;
                pricing["PricingSource"] = PricingSource;
                pricing["League"] = CurrentLeague;
                pricing["DisplayCurrency"] = DisplayCurrency;
                pricing["RedThreshold"] = RedThreshold;
                pricing["OrangeThreshold"] = OrangeThreshold;
                pricing["GreenThreshold"] = GreenThreshold;
            }

            if (rootObj["OCR"] is JsonObject ocr)
            {
                ocr["DebugOverlay"] = DebugOverlay;
                ocr["HideDebugOverlayWhenInterfaceNotDetected"] = HideDebugOverlayWhenInterfaceNotDetected;
                ocr["SaveDebugImages"] = SaveDebugImages;
                ocr["Language"] = OcrLanguage;
                ocr["OcrBackend"] = OcrBackend;
                ocr["CaptureMode"] = CaptureMode;
                ocr["ScanIntervalMs"] = ScanIntervalMs;
            }

            if (rootObj["Update"] is JsonObject update)
                update["AutoUpdate"] = AutoUpdate;

            var jsonResult = rootObj.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(_configPath, jsonResult + Environment.NewLine, Encoding.UTF8);
            return null;
        }
        catch (Exception ex)
        {
            return $"Save failed: {ex.Message}";
        }
    }

    public async Task<IReadOnlyList<string>> LoadLeaguesAsync()
    {
        try
        {
            return await LeagueListService.FetchLeaguesAsync();
        }
        catch
        {
            return [CurrentLeague];
        }
    }

    public bool ConfigHasFlag(string section, string key)
    {
        try
        {
            if (!File.Exists(_configPath)) return false;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            return root?[section]?[key]?.GetValue<bool>() == true;
        }
        catch { return false; }
    }

    public void SetConfigFlag(string section, string key, bool value)
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            if (root?[section] is JsonObject sectionObj)
            {
                sectionObj[key] = value;
                File.WriteAllText(_configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
        }
        catch { }
    }

    public string? TryGetPendingChangelogVersion()
    {
        try
        {
            if (!File.Exists(_configPath)) return null;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            var changelog = root?["Changelog"];
            if (changelog is null) return null;

            var shown = changelog["Shown"]?.GetValue<bool>() ?? true;
            if (shown) return null;

            var version = changelog["Version"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(version)) return null;

            return version;
        }
        catch { return null; }
    }

    public bool HasChangelogSection()
    {
        try
        {
            if (!File.Exists(_configPath)) return false;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            return root?["Changelog"] is not null;
        }
        catch { return false; }
    }


    public void MarkChangelogShown()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            if (root?["Changelog"] is JsonNode changelog)
            {
                changelog["Shown"] = true;
                var newJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, newJson + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }

    public (double Left, double Top, double Width, double Height)? RestoreWindowPosition()
    {
        try
        {
            if (!File.Exists(_configPath)) return null;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            if (root?["Window"] is not JsonNode window) return null;
            var left = window["Left"]?.GetValue<double>() ?? double.NaN;
            var top = window["Top"]?.GetValue<double>() ?? double.NaN;
            var width = window["Width"]?.GetValue<double>() ?? double.NaN;
            return (left, top, width, 652);
        }
        catch { return null; }
    }

    public void SaveWindowPosition(double left, double top, double width)
    {
        try
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                _ = Directory.CreateDirectory(configDir);

            JsonNode root;
            if (File.Exists(_configPath))
            {
                var existingJson = File.ReadAllText(_configPath, Encoding.UTF8);
                root = JsonNode.Parse(existingJson) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var windowNode = root["Window"] as JsonObject;
            if (windowNode is null)
            {
                windowNode = [];
                root["Window"] = windowNode;
            }

            windowNode["Left"] = (int)left;
            windowNode["Top"] = (int)top;
            windowNode["Width"] = (int)width;

            var json = root.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(_configPath, json + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    public void SaveRememberDebugPanel()
    {
        try
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                _ = Directory.CreateDirectory(configDir);

            JsonNode root;
            if (File.Exists(_configPath))
            {
                var existingJson = File.ReadAllText(_configPath, Encoding.UTF8);
                root = JsonNode.Parse(existingJson) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var appNode = root["App"] as JsonObject;
            if (appNode is null)
            {
                appNode = [];
                root["App"] = appNode;
            }

            appNode["RememberDebugPanel"] = RememberDebugPanel;
            var json = root.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(_configPath, json + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}
