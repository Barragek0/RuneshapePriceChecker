using System.Text;
using System.Text.Json.Nodes;

namespace RuneshapePriceChecker.Startup;

public static class AppSettingsBootstrapper
{
    private const string DefaultAppSettingsJson = """
{
    "App": {
        "LogLevel": "Information",
        "BringToForeground": true,
        "AlwaysOnTop": false,
        "RememberDebugPanel": false,
        "CloseWithPoE2": false,
        "OpenWithPoE2": false,
        "AutoRestartOnCrash": false,
        "UseMetadataSerialization": false,
        "AllOverlaysDisabled": false,
        "PricingOverlay": true,
        "Banner": true,
        "ForceUpdateAvailable": false,
        "AutoApplyUpdate": false,
        "TestMode": false
    },
    "Pricing": {
        "PricingSource": "poe2scout",
        "League": "Runes of Aldur",
        "AutoPriceThresholds": true,
        "RedThreshold": 0.5,
        "OrangeThreshold": 1.0,
        "GreenThreshold": 5.0,
        "DisplayCurrency": "exalt",
        "TradeVolumeWarning": true,
        "TradeVolumeMatchColor": true,
        "TradeVolumeBanner": true
    },
    "OCR": {
        "Language": "eng",
        "SaveDebugImages": false,
        "DebugOverlay": false,
        "HideDebugOverlayWhenInterfaceNotDetected": false,
        "OcrBackend": "windows",
        "CaptureMode": "printwindow",
        "TesseractDataPath": "",
        "EnableImagePreprocessing": true,
        "BinarizationThreshold": 145,
        "EnableTextColorFiltering": true,
        "TextColorTargetR": 50,
        "TextColorTargetG": 42,
        "TextColorTargetB": 34,
        "TextColorTolerance": 47,
        "TextColorMaxLuminance": 145,
        "TextColorMaxChannelSpread": 29,
        "DebugImageIntervalSeconds": 15,
        "UseRawBitmapProcessing": false,
        "DebugImageDirectory": "",
        "OcrEngineMode": 2,
        "ScanIntervalMs": 100,
        "BypassOcrCache": false,
        "PerfMetricsInterval": 0,
        "OverlayScale": null
    },
    "Update": {
        "AutoUpdate": true,
        "IgnorePrereleases": false,
        "GithubToken": null
    },
    "Window": {
        "InitialSetupComplete": false,
        "CustomOffsetX": null,
        "CustomOffsetY": null,
        "CustomWidth": null,
        "CustomHeight": null
    }
}
""";

    public static void EnsureExists()
    {
        var configDir = Path.Combine(AppContext.BaseDirectory, "config");
        _ = Directory.CreateDirectory(configDir);
        var appSettingsPath = Path.Combine(configDir, "appsettings.json");
        if (!File.Exists(appSettingsPath))
        {
            File.WriteAllText(appSettingsPath, DefaultAppSettingsJson + Environment.NewLine, Encoding.UTF8);
            return;
        }

        var existingJson = File.ReadAllText(appSettingsPath, Encoding.UTF8);
        JsonNode? existing;
        try { existing = JsonNode.Parse(existingJson); } catch { existing = null; }
        if (existing is null)
        {
            File.WriteAllText(appSettingsPath, DefaultAppSettingsJson + Environment.NewLine, Encoding.UTF8);
            return;
        }

        JsonNode? defaults;
        try { defaults = JsonNode.Parse(DefaultAppSettingsJson); } catch { return; }
        if (defaults is null) return;

        MigrateRenamedProperties(existing);

        var missing = DeepMergeDefaults(existing, defaults);

        if (missing)
        {
            var merged = existing.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(appSettingsPath, merged + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static bool DeepMergeDefaults(JsonNode existing, JsonNode defaults)
    {
        var missing = false;

        foreach (var defaultProp in defaults.AsObject())
        {
            if (!existing.AsObject().ContainsKey(defaultProp.Key))
            {
                existing[defaultProp.Key] = defaultProp.Value?.DeepClone();
                missing = true;
            }
            else if (defaultProp.Value is JsonObject defaultObj &&
                     existing[defaultProp.Key] is JsonObject existingObj)
            {
                foreach (var subProp in defaultObj)
                {
                    if (!existingObj.ContainsKey(subProp.Key))
                    {
                        existingObj[subProp.Key] = subProp.Value?.DeepClone();
                        missing = true;
                    }
                }
            }
        }

        return missing;
    }

    private static void MigrateRenamedProperties(JsonNode existing)
    {
        var renamed = false;

        if (existing["App"] is JsonNode app)
        {
            if (app["EnableDebugLogging"] is JsonValue enableVal)
            {
                app["LogLevel"] = enableVal.GetValueKind() == System.Text.Json.JsonValueKind.True ? "Debug" : "Information";
                _ = app.AsObject().Remove("EnableDebugLogging");
                renamed = true;
            }
            if (app["DebugLogging"] is JsonValue debugVal)
            {
                app["LogLevel"] = debugVal.GetValueKind() == System.Text.Json.JsonValueKind.True ? "Debug" : "Information";
                _ = app.AsObject().Remove("DebugLogging");
                renamed = true;
            }
        }

        if (existing["OCR"] is JsonNode ocr)
        {
            if (RenameKey(ocr, "ShowCaptureBoundsOverlay", "DebugOverlay")) renamed = true;

            // Migrate OverlayScale from App section to OCR section (v1.0.8+)
            var overlayAppNode = existing["App"];
            if (overlayAppNode?["OverlayScale"] is not null)
            {
                if (!ocr.AsObject().ContainsKey("OverlayScale"))
                    ocr["OverlayScale"] = overlayAppNode["OverlayScale"]!.DeepClone();
                _ = overlayAppNode.AsObject().Remove("OverlayScale");
                renamed = true;
            }

            // Migrate UseWindowClientCapture (bool, pre-1.0.2) to CaptureMode (string)
            if (ocr["UseWindowClientCapture"] is JsonValue oldCapture)
            {
                var isDesktop = oldCapture.GetValueKind() == System.Text.Json.JsonValueKind.True;
                ocr["CaptureMode"] = isDesktop ? "desktop" : "printwindow";
                _ = ocr.AsObject().Remove("UseWindowClientCapture");
                renamed = true;
            }

            // Migrate ShowPricingOverlay and ShowBanner from OCR to App section
            var appSection = existing["App"] as JsonObject;
            if (ocr["ShowPricingOverlay"] is not null && appSection is not null)
            {
                if (!appSection.ContainsKey("PricingOverlay"))
                    appSection["PricingOverlay"] = ocr["ShowPricingOverlay"]!.DeepClone();
                _ = ocr.AsObject().Remove("ShowPricingOverlay");
                renamed = true;
            }
            if (ocr["ShowBanner"] is not null && appSection is not null)
            {
                if (!appSection.ContainsKey("Banner"))
                    appSection["Banner"] = ocr["ShowBanner"]!.DeepClone();
                _ = ocr.AsObject().Remove("ShowBanner");
                renamed = true;
            }
        }

        if (existing["Update"] is JsonNode update)
        {
            if (RenameKey(update, "AutoUpdateEnabled", "AutoUpdate")) renamed = true;
        }

        // Migrate PricingCache to 1.0.0
        if (existing["PricingCache"] is JsonObject oldPricingCache)
        {
            var pricing = (existing["Pricing"] as JsonObject) ?? [];
            existing["Pricing"] = pricing;
            foreach (var kvp in oldPricingCache)
            {
                if (!pricing.ContainsKey(kvp.Key))
                    pricing[kvp.Key] = kvp.Value?.DeepClone();
            }
            _ = existing.AsObject().Remove("PricingCache");
            renamed = true;
        }

        // Remove stale Changelog.Body key (changelog is now fetched live from GitHub)
        if (existing["Changelog"] is JsonObject changelog)
        {
            if (changelog.Remove("Body"))
                renamed = true;
        }

        if (renamed)
        {
            var configDir = Path.Combine(AppContext.BaseDirectory, "config");
            var appSettingsPath = Path.Combine(configDir, "appsettings.json");
            var merged = existing.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(appSettingsPath, merged + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static bool RenameKey(JsonNode node, string oldKey, string newKey)
    {
        if (node[oldKey] is null)
            return false;

        node[newKey] = node[oldKey]!.DeepClone();
        _ = node.AsObject().Remove(oldKey);
        return true;
    }

    // If the app crashed or was closed during a bug report, a settings snapshot
    // file (*-report-snapshot.*.json) may still exist in the config directory.
    // Restore it over the active appsettings.json so the app starts with the
    // original settings, not the diagnostic-mode settings left by the bug report.
    public static void TryRecoverBugReportSnapshot()
    {
        try
        {
            var configDir = Path.Combine(AppContext.BaseDirectory, "config");
            var configPath = Path.Combine(configDir, "appsettings.json");

            var snapshots = Directory.GetFiles(configDir, "bug-report-snapshot.*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();

            if (snapshots.Length == 0)
                return;

            // Restore the most recent snapshot
            var snapshot = snapshots[0];
            File.Copy(snapshot, configPath, overwrite: true);
            File.Delete(snapshot);

            // Clean up any older stale snapshots
            for (var i = 1; i < snapshots.Length; i++)
                try { File.Delete(snapshots[i]); } catch { }
        }
        catch
        {
            // Non-critical — the app will start with whatever settings exist.
        }
    }
}
