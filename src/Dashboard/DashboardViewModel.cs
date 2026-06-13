using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
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
    public string DisplayCurrency { get; set; } = "chaos";
    public decimal RedThreshold { get; set; } = 0.5m;
    public decimal OrangeThreshold { get; set; } = 1.0m;
    public decimal GreenThreshold { get; set; } = 5.0m;
    public string LogLevel { get; set; } = "Information";
    public bool DebugOverlay { get; set; }
    public bool HideDebugOverlayWhenInterfaceNotDetected { get; set; }
    public bool SaveDebugImages { get; set; }
    public bool ShowPricingOverlay { get; set; } = true;
    public bool ShowBanner { get; set; } = true;
    public string OcrLanguage { get; set; } = "eng";
    public bool AutoUpdate { get; set; } = true;

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
                LogLevel = app.Str("LogLevel", "Information");

            if (root["Pricing"] is JsonNode pricing)
            {
                CurrentLeague = pricing.Str("League", "Runes of Aldur");
                PricingSource = pricing.Str("PricingSource", "poe2scout");
                DisplayCurrency = pricing.Str("DisplayCurrency", "chaos");
                RedThreshold = pricing.Val("RedThreshold", 0.5m);
                OrangeThreshold = pricing.Val("OrangeThreshold", 1.0m);
                GreenThreshold = pricing.Val("GreenThreshold", 5.0m);
            }

            if (root["OCR"] is JsonNode ocr)
            {
                DebugOverlay = ocr.Val("DebugOverlay", false);
                HideDebugOverlayWhenInterfaceNotDetected = ocr.Val("HideDebugOverlayWhenInterfaceNotDetected", false);
                SaveDebugImages = ocr.Val("SaveDebugImages", false);
                ShowPricingOverlay = ocr.Val("ShowPricingOverlay", true);
                ShowBanner = ocr.Val("ShowBanner", true);
                OcrLanguage = ocr.Str("Language", "eng");
            }

            if (root["Update"] is JsonNode update)
                AutoUpdate = update.Val("AutoUpdate", true);
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
            Directory.CreateDirectory(configDir);

            var existingJson = File.Exists(_configPath) ? File.ReadAllText(_configPath, Encoding.UTF8) : "{}";
            var root = JsonNode.Parse(existingJson) ?? new JsonObject();
            if (root is not JsonObject rootObj) return null;

            var previousLevel = rootObj["App"]?.Str("LogLevel", "Information") ?? "Information";
            LogLevelChanged = !string.Equals(previousLevel, LogLevel, StringComparison.OrdinalIgnoreCase);

            rootObj["App"] ??= new JsonObject();
            rootObj["Pricing"] ??= new JsonObject();
            rootObj["OCR"] ??= new JsonObject();
            rootObj["Update"] ??= new JsonObject();

            if (rootObj["App"] is JsonObject app)
                app["LogLevel"] = LogLevel;

            if (rootObj["Pricing"] is JsonObject pricing)
            {
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
                ocr["ShowPricingOverlay"] = ShowPricingOverlay;
                ocr["ShowBanner"] = ShowBanner;
                ocr["Language"] = OcrLanguage;
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

    public (string? Body, string? Version)? TryGetPendingChangelog()
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

            var body = changelog["Body"]?.GetValue<string>();
            var version = changelog["Version"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(body)) return null;

            return (body, version);
        }
        catch { return null; }
    }

    public (string? Body, string? Version)? GetCachedChangelog()
    {
        try
        {
            if (!File.Exists(_configPath)) return null;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            var changelog = root?["Changelog"];
            if (changelog is null) return null;

            var body = changelog["Body"]?.GetValue<string>();
            var version = changelog["Version"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(body)) return null;

            return (body, version);
        }
        catch { return null; }
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
                var newJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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
            var height = window["Height"]?.GetValue<double>() ?? double.NaN;
            return (left, top, width, height);
        }
        catch { return null; }
    }

    public void SaveWindowPosition(double left, double top, double width, double height)
    {
        try
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);

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
            windowNode["Height"] = (int)height;

            var json = root.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(_configPath, json + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}
