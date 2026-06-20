using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Pricing;

public sealed class TranslationCache : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TranslationCache> _logger;
    private readonly string _ocrDir;
    private ConcurrentDictionary<string, string> _translations = new(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<string, string> _translationsNoDiacritics = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    private static readonly Dictionary<string, string> LangFileMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fra"] = "fra",
        ["fr"] = "fra",
        ["deu"] = "deu",
        ["de"] = "deu",
        ["spa"] = "spa",
        ["es"] = "spa",
        ["por"] = "por",
        ["pt"] = "por",
        ["rus"] = "rus",
        ["ru"] = "rus",
        ["kor"] = "kor",
        ["ko"] = "kor",
        ["jpn"] = "jpn",
        ["ja"] = "jpn",
        ["chi_tra"] = "chi_tra",
        ["cmn-hant"] = "chi_tra",
        ["cht"] = "chi_tra",
    };

    public bool IsLoaded => LoadedLanguage is not null;
    public string? LoadedLanguage { get; private set; }
    public int Count => _translations.Count;

    public TranslationCache(HttpClient httpClient, ILogger<TranslationCache> logger)
        : this(httpClient, logger, Path.Combine(AppContext.BaseDirectory, "ocr"))
    {
    }

    public TranslationCache(HttpClient httpClient, ILogger<TranslationCache> logger, string? ocrDir)
    {
        _httpClient = httpClient;
        _logger = logger;
        _ocrDir = ocrDir ?? Path.Combine(AppContext.BaseDirectory, "ocr");
        _ = Directory.CreateDirectory(_ocrDir);
    }

    public async Task LoadAsync(string language, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(language) ||
            language.Equals("eng", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            _translations = [];
            _translationsNoDiacritics = [];
            LoadedLanguage = null;
            return;
        }

        if (string.Equals(LoadedLanguage, language, StringComparison.OrdinalIgnoreCase))
            return;

        if (!LangFileMap.TryGetValue(language, out var fileName))
        {
            _logger.LogWarning("TranslationCache: unsupported language '{Lang}'", language);
            _translations = [];
            _translationsNoDiacritics = [];
            LoadedLanguage = null;
            return;
        }

        var dataPath = Path.Combine(_ocrDir, $"{language}-translation.dat");
        var hashPath = Path.Combine(_ocrDir, $"{language}.hash");

        var ndjson = TryReadNdjson(fileName);
        if (ndjson is not null)
        {
            var newHash = ComputeHash(ndjson);

            if (File.Exists(hashPath))
            {
                var existingHash = await File.ReadAllTextAsync(hashPath, ct).ConfigureAwait(false);
                if (string.Equals(existingHash.Trim(), newHash, StringComparison.OrdinalIgnoreCase))
                {
                    _translations = await LoadDataFileAsync(dataPath).ConfigureAwait(false);
                    RebuildNoDiacriticsMap();
                    LoadedLanguage = language;
                    _logger.LogInformation("TranslationCache: loaded {Count} translations for '{Lang}' from cache", _translations.Count, language);
                    return;
                }
            }

            _logger.LogInformation("TranslationCache: processing '{Lang}'...", language);
            var processed = ParseNdjson(ndjson);

            await File.WriteAllTextAsync(hashPath, newHash, ct).ConfigureAwait(false);
            await SaveDataFileAsync(dataPath, processed).ConfigureAwait(false);

            _translations = processed;
            RebuildNoDiacriticsMap();
            LoadedLanguage = language;
            _logger.LogInformation("TranslationCache: loaded {Count} translations for '{Lang}'", _translations.Count, language);
            return;
        }

        _logger.LogWarning("TranslationCache: no ndjson data for '{Lang}', trying cached .dat", language);
        await TryLoadCachedAsync(dataPath, language).ConfigureAwait(false);
    }

    public void LoadFromString(string language, string ndjson)
    {
        if (string.IsNullOrEmpty(language) ||
            language.Equals("eng", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            _translations = [];
            _translationsNoDiacritics = [];
            LoadedLanguage = null;
            return;
        }

        var processed = ParseNdjson(ndjson);
        _translations = processed;
        RebuildNoDiacriticsMap();
        LoadedLanguage = language;
    }

    private static string? TryReadNdjson(string fileName)
    {
        var resourceName = $"translations.{fileName}.ndjson";

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(resourceName)
                    ?? asm.GetManifestResourceStream($"RuneshapePriceChecker.{resourceName}");
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            }
            catch { }
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ocr", "translations", $"{fileName}.ndjson"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ocr", "translations", $"{fileName}.ndjson"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ocr", "translations", $"{fileName}.ndjson"),
        };

        foreach (var candidate in candidates)
        {
            var resolved = Path.GetFullPath(candidate);
            if (File.Exists(resolved))
                return File.ReadAllText(resolved);
        }

        return null;
    }

    private static ConcurrentDictionary<string, string> ParseNdjson(string ndjson)
    {
        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reader = new StringReader(ndjson);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var name = GetStringFromElement(root, "name");
            var refName = GetStringFromElement(root, "refName");

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(refName))
                continue;

            if (string.Equals(name, refName, StringComparison.OrdinalIgnoreCase))
                continue;

            result[name] = refName;
        }

        return result;
    }

    private async Task TryLoadCachedAsync(string dataPath, string language)
    {
        if (File.Exists(dataPath))
        {
            _logger.LogInformation("TranslationCache: loading cached .dat for '{Lang}'", language);
            _translations = await LoadDataFileAsync(dataPath).ConfigureAwait(false);
            RebuildNoDiacriticsMap();
            LoadedLanguage = language;
        }
    }

    public string? ToEnglish(string name)
    {
        if (_translations.TryGetValue(name, out var english))
            return english;

        var plain = RemoveDiacritics(name);
        if (_translationsNoDiacritics.TryGetValue(plain, out english))
            return english;

        var noApos = RemoveApostrophes(plain);
        if (noApos != plain && _translationsNoDiacritics.TryGetValue(noApos, out english))
            return english;

        var withoutTier = StripTierSuffix(plain);
        if (withoutTier is not null && _translationsNoDiacritics.TryGetValue(withoutTier, out english))
            return english;

        foreach (var kvp in _translationsNoDiacritics)
        {
            if (StrComp.IsOneCharAway(plain, kvp.Key))
                return kvp.Value;
            if (noApos != plain && StrComp.IsOneCharAway(noApos, kvp.Key))
                return kvp.Value;
        }

        for (var maxDist = 2; maxDist <= 3; maxDist++)
        {
            foreach (var kvp in _translationsNoDiacritics)
            {
                if (StrComp.AreFewCharsAway(plain, kvp.Key, maxDist))
                    return kvp.Value;
                if (noApos != plain && StrComp.AreFewCharsAway(noApos, kvp.Key, maxDist))
                    return kvp.Value;
            }
        }

        // Some OCR errors insert extra spaces ("Aproveita mento" instead of "Aproveitamento")
        // or miss spaces entirely. Strip spaces from both sides as a last-resort fallback.
        var noSpaces = RemoveAllSpaces(plain);
        if (noSpaces != plain)
        {
            foreach (var kvp in _translationsNoDiacritics)
            {
                var keyNoSpaces = RemoveAllSpaces(kvp.Key);
                if (keyNoSpaces == kvp.Key) continue;
                if (StrComp.IsOneCharAway(noSpaces, keyNoSpaces))
                    return kvp.Value;
            }

            for (var maxDist = 2; maxDist <= 3; maxDist++)
            {
                foreach (var kvp in _translationsNoDiacritics)
                {
                    var keyNoSpaces = RemoveAllSpaces(kvp.Key);
                    if (keyNoSpaces == kvp.Key) continue;
                    if (StrComp.AreFewCharsAway(noSpaces, keyNoSpaces, maxDist))
                        return kvp.Value;
                }
            }
        }

        // Some items (e.g. Thaumaturgisches Flussmittel) only exist in the ndjson
        // WITH a level suffix like "(Stufe 10)" but have no base-name-only entry.
        // Strip level suffixes from dictionary keys as a last-resort fallback,
        // then apply fuzzy matching — the OCR may still have errors in the base name.
        foreach (var kvp in _translationsNoDiacritics)
        {
            var strippedKey = StripLevelSuffixFromKey(kvp.Key);
            if (strippedKey is null) continue;

            if (string.Equals(plain, strippedKey, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
            if (noApos != plain && string.Equals(noApos, strippedKey, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;

            if (StrComp.IsOneCharAway(plain, strippedKey))
                return kvp.Value;
            if (noApos != plain && StrComp.IsOneCharAway(noApos, strippedKey))
                return kvp.Value;

            for (var maxDist = 2; maxDist <= 3; maxDist++)
            {
                if (StrComp.AreFewCharsAway(plain, strippedKey, maxDist))
                    return kvp.Value;
                if (noApos != plain && StrComp.AreFewCharsAway(noApos, strippedKey, maxDist))
                    return kvp.Value;
            }

            // Space-stripped fallback within level-suffix section
            if (noSpaces != plain)
            {
                var strippedNoSpaces = RemoveAllSpaces(strippedKey);
                if (strippedNoSpaces != strippedKey)
                {
                    if (StrComp.IsOneCharAway(noSpaces, strippedNoSpaces))
                        return kvp.Value;
                    for (var maxDist = 2; maxDist <= 3; maxDist++)
                    {
                        if (StrComp.AreFewCharsAway(noSpaces, strippedNoSpaces, maxDist))
                            return kvp.Value;
                    }
                }
            }
        }

        return null;
    }

    private static string? StripLevelSuffixFromKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var lastParen = key.LastIndexOf('(');
        if (lastParen < 0) return null;
        // Pattern: open-paren, some word chars, space, digits, close-paren at end
        var suffix = key.AsSpan(lastParen);
        var closeParen = suffix.LastIndexOf(')');
        if (closeParen < 0 || closeParen != suffix.Length - 1) return null;
        // Extract the content between parens
        var inner = suffix[1..^1].Trim();
        var spaceIdx = inner.LastIndexOf(' ');
        if (spaceIdx < 0) return null;
        var word = inner[..spaceIdx];
        var num = inner[(spaceIdx + 1)..];
        if (word.Length > 0 && int.TryParse(num, out _))
        {
            var stripped = key.AsSpan(0, lastParen).TrimEnd();
            return stripped.Length > 0 ? stripped.ToString() : null;
        }
        return null;
    }

    private static string? GetStringFromElement(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static string? StripTierSuffix(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var suffix in TierSuffixes)
        {
            var patternEnd = text.AsSpan().TrimEnd();
            var sSuffix = suffix.AsSpan();
            var idx = patternEnd.LastIndexOf(sSuffix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var afterSuffix = patternEnd[(idx + sSuffix.Length)..].TrimStart();
            if (afterSuffix.Length > 0 && !int.TryParse(afterSuffix, out _))
                continue;

            return patternEnd[..idx].TrimEnd().ToString();
        }
        return null;
    }

    private static readonly string[] TierSuffixes = ["majeur", "mineur", "parfait", "supérieur", "supérieure", "stufe", "niveau", "nivel", "nível", "уровень"];

    private static string RemoveApostrophes(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\'') < 0)
            return text;
        return text.Replace('\'', ' ');
    }

    private static string RemoveAllSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        for (var i = 0; i < text.Length; i++)
            if (char.IsWhiteSpace(text[i]))
                goto needsStrip;
        return text;
    needsStrip:
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsWhiteSpace(c))
                _ = sb.Append(c);
        return sb.ToString();
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

    private void RebuildNoDiacriticsMap()
    {
        var map = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _translations)
        {
            var plain = RemoveDiacritics(kvp.Key);
            map[plain] = kvp.Value;
            var noApos = RemoveApostrophes(plain);
            if (noApos != plain)
                map[noApos] = kvp.Value;
        }
        _translationsNoDiacritics = map;
    }

    private static string ComputeHash(string data)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task SaveDataFileAsync(string path, ConcurrentDictionary<string, string> data)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await using var fileStream = File.Create(path);
        await using var gzip = new GZipStream(fileStream, CompressionLevel.SmallestSize);
        await gzip.WriteAsync(bytes);
    }

    private static async Task<ConcurrentDictionary<string, string>> LoadDataFileAsync(string path)
    {
        await using var fileStream = File.OpenRead(path);
        await using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var json = await reader.ReadToEndAsync();

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions);
        return dict is not null
            ? new ConcurrentDictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase)
            : new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    public void WatchForChanges()
    {
        try
        {
            if (!Directory.Exists(_ocrDir)) return;
            _watcher = new FileSystemWatcher(_ocrDir, "*-translation.dat")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += async (s, e) =>
            {
                try
                {
                    if (LoadedLanguage is null) return;
                    var langDataPath = Path.Combine(_ocrDir, $"{LoadedLanguage}-translation.dat");
                    if (!string.Equals(e.FullPath, langDataPath, StringComparison.OrdinalIgnoreCase))
                        return;
                    _logger.LogInformation("TranslationCache: {File} changed — reloading", e.Name);
                    var data = await LoadDataFileAsync(langDataPath).ConfigureAwait(false);
                    _translations = data;
                    RebuildNoDiacriticsMap();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TranslationCache: failed to reload changed .dat");
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TranslationCache: failed to create file watcher");
        }
    }

    private FileSystemWatcher? _watcher;
}
