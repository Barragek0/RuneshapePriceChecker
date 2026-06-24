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
        "AllOverlaysDisabled": false,
        "PricingOverlay": true,
        "Banner": true
    },
    "Pricing": {
        "PricingSource": "poe2scout",
        "League": "Runes of Aldur",
        "AutoPriceThresholds": true,
        "RedThreshold": 0.5,
        "OrangeThreshold": 1.0,
        "GreenThreshold": 5.0,
        "DisplayCurrency": "exalt"
    },
    "OCR": {
        "Language": "eng",
        "SaveDebugImages": false,
        "DebugOverlay": false,
        "HideDebugOverlayWhenInterfaceNotDetected": false,
        "OcrBackend": "windows",
        "CaptureMode": "printwindow"
    },
    "Update": {
        "AutoUpdate": true
    },
    "Window": {
        "InitialSetupComplete": false
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
            _ = ocr.AsObject().Remove("BinarizationThreshold");

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
}
