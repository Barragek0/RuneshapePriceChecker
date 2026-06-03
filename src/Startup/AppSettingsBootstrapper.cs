using System.Text;
using System.Text.Json.Nodes;

namespace RuneshapePriceChecker.Startup;

public static class AppSettingsBootstrapper
{
    private const string DefaultAppSettingsJson = """
{
    "App": {
        "EnableDebugLogging": false
    },
    "Pricing": {
        "League": "Runes of Aldur",
        "RedThreshold": 0.5,
        "OrangeThreshold": 1.0,
        "GreenThreshold": 5.0,
        "DisplayCurrency": "exalt"
    },
    "OCR": {
        "Language": "eng",
        "SaveDebugImages": false,
        "ShowCaptureBoundsOverlay": false
    },
    "Update": {
        "AutoUpdateEnabled": true,
        "IgnorePrereleases": false
    }
}
""";

    public static void EnsureExists()
    {
        var configDir = Path.Combine(AppContext.BaseDirectory, "config");
        Directory.CreateDirectory(configDir);
        var appSettingsPath = Path.Combine(configDir, "appsettings.json");
        if (!File.Exists(appSettingsPath))
        {
            File.WriteAllText(appSettingsPath, DefaultAppSettingsJson + Environment.NewLine, Encoding.UTF8);
            return;
        }

        var existingJson = File.ReadAllText(appSettingsPath, Encoding.UTF8);
        var existing = JsonNode.Parse(existingJson);
        var defaults = JsonNode.Parse(DefaultAppSettingsJson);
        if (existing is null || defaults is null) return;

        var missing = false;
        foreach (var defaultProp in defaults.AsObject())
        {
            if (!existing.AsObject().ContainsKey(defaultProp.Key))
            {
                existing[defaultProp.Key] = defaultProp.Value?.DeepClone();
                missing = true;
            }
        }

        if (missing)
        {
            var merged = existing.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(appSettingsPath, merged + Environment.NewLine, Encoding.UTF8);
        }
    }
}
