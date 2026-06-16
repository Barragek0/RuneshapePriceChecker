using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Pricing;

/// <summary>
/// Fetches and caches item name translations from the official Path of Exile trade API.
/// Maps non-English item names (e.g. "Orbe du Chaos") to their English equivalents ("Chaos Orb")
/// so OCR results in any language can be matched against English pricing data.
/// </summary>
public sealed class ItemNameTranslator(HttpClient httpClient, ILogger<ItemNameTranslator> logger)
{
    private readonly ConcurrentDictionary<string, string> _translations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _reverse = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _loaded;
    private string? _loadedLanguage;

    public bool IsLoaded => _loaded;

    /// <summary>
    /// Translates an item name from the game's language to English.
    /// Returns the original name unchanged if no translation is found or if language is English.
    /// Triggers lazy loading on first call.
    /// </summary>
    public string ToEnglish(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (!_loaded && _pendingLanguage is not null)
        {
            // Trigger background load (fire-and-forget — next call will have data)
            _ = LoadAsync(_pendingLanguage, CancellationToken.None);
            return name;
        }
        if (!_loaded) return name;
        return _translations.TryGetValue(name, out var english) ? english : name;
    }

    private string? _pendingLanguage;

    /// <summary>
    /// Sets the target language. Call this when OCR language changes.
    /// </summary>
    public void SetLanguage(string language)
    {
        if (string.IsNullOrEmpty(language) || language.Equals("eng", StringComparison.OrdinalIgnoreCase))
        {
            _loaded = true;
            _loadedLanguage = "eng";
            _translations.Clear();
            return;
        }

        if (_loaded && string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase))
            return;

        _pendingLanguage = language;
        _loaded = false;
    }

    /// <summary>
    /// Fetches the complete item name dictionary for the given language from the trade API.
    /// Only re-fetches if the language changed since the last load.
    /// </summary>
    public async Task LoadAsync(string language, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(language) || language.Equals("eng", StringComparison.OrdinalIgnoreCase))
        {
            _loaded = true;
            _loadedLanguage = "eng";
            logger.LogInformation("ItemNameTranslator: English selected, no translation needed.");
            return;
        }

        if (_loaded && string.Equals(_loadedLanguage, language, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var domain = GetDomainForLanguage(language);
            var url = $"https://{domain}/api/trade2/data/items";

            logger.LogInformation("ItemNameTranslator: fetching item names for '{Lang}' from {Url}...", language, url);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            _ = request.Headers.AcceptLanguage.TryParseAdd(language);

            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            ParseItems(json);

            _loaded = true;
            _loadedLanguage = language;
            logger.LogInformation("ItemNameTranslator: loaded {Count} translated item names for '{Lang}'", _translations.Count, language);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ItemNameTranslator: failed to load translations for '{Lang}'. OCR results will not be translated.", language);
            _loaded = true; // prevent retry spam, just work in English-only mode
            _loadedLanguage = language;
        }
    }

    private void ParseItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("result");

        _translations.Clear();
        _reverse.Clear();

        foreach (var category in results.EnumerateArray())
        {
            if (!category.TryGetProperty("entries", out var entries)) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                var english = GetString(entry, "type") ?? GetString(entry, "text");
                var translated = GetString(entry, "text");

                if (string.IsNullOrEmpty(english) || string.IsNullOrEmpty(translated))
                    continue;

                // text field is the localized name, type field is the English internal name
                // Store both directions for robustness
                if (!string.Equals(english, translated, StringComparison.OrdinalIgnoreCase))
                {
                    _translations[translated] = english;
                    _reverse[english] = translated;
                }
            }
        }

        // Add known currency mappings that the API doesn't include in items
        AddManualMappings();
    }

    private void AddManualMappings()
    {
        // Common currency names that the trade API may not list
        var manual = new (string English, string[] Translated)[]
        {
            ("Chaos Orb", ["Orbe du Chaos", "Chaos-Kugel", "Orbe del Caos", "Orbe do Caos", "Сфера Хаоса", "カオスオーブ", "混沌石"]),
            ("Divine Orb", ["Orbe Divin", "Göttliche Kugel", "Orbe Divino", "Orbe Divino", "Сфера Божеств", "ディヴァインオーブ", "神聖石"]),
            ("Exalted Orb", ["Orbe Exalté", "Erhabene Kugel", "Orbe Exaltado", "Orbe Exaltado", "Сфера Возвышения", "エグザルトオーブ", "崇高石"]),
            ("Mirror of Kalandra", ["Miroir de Kalandra", "Spiegel von Kalandra", "Espejo de Kalandra", "Espelho de Kalandra", "Зеркало Каландры", "カランドラの鏡", "卡蘭德的魔鏡"]),
        };

        foreach (var (english, translated) in manual)
        {
            foreach (var t in translated)
            {
                _translations[t] = english;
                _reverse[english] = t;
            }
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static string GetDomainForLanguage(string language)
    {
        return language switch
        {
            "ru" => "ru.pathofexile.com",
            "de" => "de.pathofexile.com",
            "fr" => "fr.pathofexile.com",
            "es" => "es.pathofexile.com",
            "pt" => "br.pathofexile.com",
            "ko" => "poe.game.daum.net",
            "ja" => "jp.pathofexile.com",
            "cmn-Hant" => "pathofexile.tw",
            _ => "www.pathofexile.com"
        };
    }
}
