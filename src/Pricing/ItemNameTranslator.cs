using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Pricing;

public sealed class ItemNameTranslator(ILogger<ItemNameTranslator> logger, TranslationCache? cache = null) : IDisposable
{
    private readonly TranslationCache? _cache = cache;
    private readonly ILogger<ItemNameTranslator> _logger = logger;
    private static Lazy<Dictionary<string, string>> _bundledTranslations = new(LoadBundledTranslations);
    private static Lazy<Dictionary<string, LanguageInfo>> _languageInfo = new(LoadLanguageInfo);

    public bool IsLoaded { get; private set; }
    public string? LoadedLanguage { get; private set; }

    public static IReadOnlyDictionary<string, LanguageInfo> Languages => _languageInfo.Value;

    public static void ReloadBundledTranslations()
    {
        _bundledTranslations = new Lazy<Dictionary<string, string>>(LoadBundledTranslations);
        _languageInfo = new Lazy<Dictionary<string, LanguageInfo>>(LoadLanguageInfo);
    }

    public string ToEnglish(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // 1) Try TranslationCache (API-fetched, fastest)
        if (_cache is not null && LoadedLanguage is not null)
        {
            var cached = _cache.ToEnglish(name);
            if (cached is not null) return cached;
        }

        // 2) Try bundled translations.json fallback
        var lang = LoadedLanguage ?? "eng";
        if (!lang.Equals("eng", StringComparison.OrdinalIgnoreCase) && _bundledTranslations.Value.Count > 0)
        {
            var key = $"{name}##{lang}";
            if (_bundledTranslations.Value.TryGetValue(key, out var bundledEnglish))
                return bundledEnglish;

            var norm = NormalizeToTesseractCode(lang);
            if (norm is not null)
            {
                key = $"{name}##{norm}";
                if (_bundledTranslations.Value.TryGetValue(key, out bundledEnglish))
                    return bundledEnglish;
            }

            // Try diacritics-insensitive fallback (e.g. "Orbe exalt" matches "Orbe exalté")
            var plain = RemoveDiacritics(name);
            if (!string.Equals(plain, name, StringComparison.OrdinalIgnoreCase))
            {
                key = $"{plain}##{lang}";
                if (_bundledTranslations.Value.TryGetValue(key, out bundledEnglish))
                    return bundledEnglish;

                if (norm is not null)
                {
                    key = $"{plain}##{norm}";
                    if (_bundledTranslations.Value.TryGetValue(key, out bundledEnglish))
                        return bundledEnglish;
                }
            }

            // Try apostrophe-normalized (handles "l t" → "l'été")
            var noApos = RemoveApostrophes(plain);
            if (noApos != plain)
            {
                key = $"{noApos}##{lang}";
                if (_bundledTranslations.Value.TryGetValue(key, out bundledEnglish))
                    return bundledEnglish;

                if (norm is not null)
                {
                    key = $"{noApos}##{norm}";
                    if (_bundledTranslations.Value.TryGetValue(key, out bundledEnglish))
                        return bundledEnglish;
                }
            }
        }

        // 3) Not found — return original
        return name;
    }
    private static string RemoveApostrophes(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\'') < 0)
            return text;
        return text.Replace('\'', ' ');
    }

    private static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(text.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                _ = sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string? NormalizeToTesseractCode(string lang)
    {
        return lang.ToLowerInvariant() switch
        {
            "ru" => "rus",
            "de" => "deu",
            "fr" => "fra",
            "es" => "spa",
            "pt" => "por",
            "ko" => "kor",
            "ja" => "jpn",
            "cmn-hant" => "chi_tra",
            "cht" => "chi_tra",
            _ => null
        };
    }

    public void SetLanguage(string language)
    {
        if (string.IsNullOrEmpty(language) || language.Equals("eng", StringComparison.OrdinalIgnoreCase))
        {
            IsLoaded = true;
            LoadedLanguage = "eng";
            return;
        }

        if (IsLoaded && string.Equals(LoadedLanguage, language, StringComparison.OrdinalIgnoreCase))
            return;

        IsLoaded = false;
        LoadedLanguage = language;
    }
    public async Task LoadAsync(string language, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(language) || language.Equals("eng", StringComparison.OrdinalIgnoreCase))
        {
            IsLoaded = true;
            LoadedLanguage = "eng";
            _logger.LogInformation("ItemNameTranslator: English selected, no translation needed.");
            return;
        }

        if (IsLoaded && string.Equals(LoadedLanguage, language, StringComparison.OrdinalIgnoreCase))
            return;

        LoadedLanguage = language;

        if (_cache is not null)
        {
            await _cache.LoadAsync(language, ct).ConfigureAwait(false);
            if (_cache.IsLoaded)
            {
                IsLoaded = true;
                _logger.LogInformation("ItemNameTranslator: {Count} translations loaded via cache for '{Lang}'", _cache.Count, language);
                return;
            }
        }

        // No cache or cache failed — mark loaded to prevent retry spam
        // (bundled fallback still works in ToEnglish)
        IsLoaded = true;
        _logger.LogWarning("ItemNameTranslator: no TranslationCache available for '{Lang}', using bundled fallback only", language);
    }
    public void WatchForChanges()
    {
        // Watch bundled translations.json
        var translationsPath = FindTranslationsPath();
        if (translationsPath is not null)
        {
            var dir = Path.GetDirectoryName(translationsPath);
            var fileName = Path.GetFileName(translationsPath);
            if (dir is not null)
            {
                var jsonWatcher = new FileSystemWatcher(dir, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                var lastReload = DateTime.MinValue;
                jsonWatcher.Changed += (_, e) =>
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastReload).TotalMilliseconds < 500) return;
                    lastReload = now;

                    Thread.Sleep(100);
                    try
                    {
                        ReloadBundledTranslations();
                        _logger.LogInformation("ItemNameTranslator: translations.json changed — reloaded.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to reload translations.json");
                    }
                };
            }
        }

        // Also watch TranslationCache .dat files
        _cache?.WatchForChanges();
    }

    private static string? FindTranslationsPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "ocr", "tesseract", "translations.json"),
            Path.Combine(baseDir, "..", "..", "..", "ocr", "tesseract", "translations.json"),
            Path.Combine(baseDir, "..", "..", "..", "src", "Pricing", "translations.json"),
        };

        foreach (var candidate in candidates)
        {
            var resolved = Path.GetFullPath(candidate);
            if (File.Exists(resolved))
                return resolved;
        }

        return null;
    }

    private static Stream? OpenTranslationsStream()
    {
        // Try embedded resource (from main assembly)
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("ocr.tesseract.translations.json")
            ?? assembly.GetManifestResourceStream("tesseract.translations.json")
            ?? assembly.GetManifestResourceStream("RuneshapePriceChecker.ocr.tesseract.translations.json")
            ?? assembly.GetManifestResourceStream("RuneshapePriceChecker.tesseract.translations.json");
        if (stream is not null) return stream;

        // Also try calling assembly (test project might inherit via reference)
        try
        {
            var callingAsm = Assembly.GetCallingAssembly();
            if (callingAsm != assembly)
            {
                stream = callingAsm.GetManifestResourceStream("ocr.tesseract.translations.json")
                    ?? callingAsm.GetManifestResourceStream("tesseract.translations.json");
                if (stream is not null) return stream;
            }
        }
        catch { }

        // Fall back to file system for development / testing
        var rootSearchPaths = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."),
            Path.Combine(AppContext.BaseDirectory, "..", "..", ".."),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."),
        };

        foreach (var basePath in rootSearchPaths)
        {
            var resolved = Path.GetFullPath(basePath);
            var candidates = new[]
            {
                Path.Combine(resolved, "ocr", "tesseract", "translations.json"),
                Path.Combine(resolved, "src", "Pricing", "translations.json"),
                Path.Combine(resolved, "Pricing", "translations.json"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return File.OpenRead(candidate);
            }
        }

        return null;
    }

    private static Dictionary<string, string> LoadBundledTranslations()
    {
        try
        {
            using var stream = OpenTranslationsStream();
            if (stream is null) return [];

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var english = GetString(item, "name");
                    if (string.IsNullOrEmpty(english)) continue;

                    foreach (var prop in item.EnumerateObject())
                    {
                        if (prop.Name == "name" || prop.Value.ValueKind != JsonValueKind.String)
                            continue;
                        var langCode = prop.Name;
                        var translated = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(translated) && !string.Equals(translated, english, StringComparison.OrdinalIgnoreCase))
                        {
                            result[$"{translated}##{langCode}"] = english;
                        }
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load bundled translations: {ex.Message}");
            return [];
        }
    }

    private static Dictionary<string, LanguageInfo> LoadLanguageInfo()
    {
        // The languages section was removed from translations.json since the
        // language dropdown was eliminated. Return the hardcoded defaults.
        return GetDefaultLanguageInfo();
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static Dictionary<string, LanguageInfo> GetDefaultLanguageInfo()
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = new("English", true),
            ["fra"] = new("Français", true),
            ["deu"] = new("Deutsch", true),
            ["por"] = new("Português", true),
            ["rus"] = new("Русский", true),
            ["tha"] = new("ไทย", true),
            ["chi_tra"] = new("繁體中文", true),
            ["spa"] = new("Español", true),
            ["kor"] = new("한국어", true),
            ["jpn"] = new("日本語", true),
        };
    }

    public void Dispose()
    {
        _cache?.Dispose();
    }
}

public sealed record LanguageInfo(string DisplayName, bool SupportsWindowsOcr);
