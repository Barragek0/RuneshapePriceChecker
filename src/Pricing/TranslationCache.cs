using System.Collections.Concurrent;
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
        {
            _logger.LogTrace("TCache: exact '{Name}' -> '{Result}'", name, english);
            return english;
        }

        // Level 2: Diacritics-insensitive — all languages
        var plain = FallbackProvider.RemoveDiacritics(name);
        if (_translationsNoDiacritics.TryGetValue(plain, out english))
        {
            _logger.LogTrace("TCache: diacritics '{Name}' -> '{Result}'", plain, english);
            return english;
        }

        // Level 3: Apostrophe-normalized — critical for fra
        var noApos = FallbackProvider.RemoveApostrophes(plain);
        if (noApos != plain && _translationsNoDiacritics.TryGetValue(noApos, out english))
        {
            _logger.LogTrace("TCache: apostrophe '{Name}' -> '{Result}'", noApos, english);
            return english;
        }

        // Level 4: Tier suffix stripping — critical for fra/deu
        var withoutTier = StripTierSuffix(plain);
        if (withoutTier is not null && _translationsNoDiacritics.TryGetValue(withoutTier, out english))
        {
            _logger.LogTrace("TCache: tier '{Name}' -> '{Result}'", withoutTier, english);
            return english;
        }

        // Levels 5-6: Fuzzy match with adaptive distance based on string length
        var fuzzyDist = FallbackProvider.FuzzyMaxDist(plain);
        var mcResult = FallbackProvider.TryMultiCharTranslation(plain, _translationsNoDiacritics, fuzzyDist);
        if (mcResult is not null) { _logger.LogTrace("TCache: fuzzy '{Name}' -> '{Result}' (d={D})", plain, mcResult, fuzzyDist); return mcResult; }
        if (noApos != plain)
        {
            mcResult = FallbackProvider.TryMultiCharTranslation(noApos, _translationsNoDiacritics, fuzzyDist);
            if (mcResult is not null) { _logger.LogTrace("TCache: fuzzy(noApos) '{Name}' -> '{Result}' (d={D})", noApos, mcResult, fuzzyDist); return mcResult; }
        }

        // Level 7: Space-stripped + fuzzy — critical for por ("Aproveita mento")
        var noSpaces = FallbackProvider.RemoveAllSpaces(plain);
        if (noSpaces != plain)
        {
            var spaceResult = FallbackProvider.TrySpaceStrippedMultiCharTranslation(plain, noSpaces, _translationsNoDiacritics, fuzzyDist);
            if (spaceResult is not null) { _logger.LogTrace("TCache: space-stripped '{Name}' -> '{Result}'", noSpaces, spaceResult); return spaceResult; }
        }

        // Level 8: Level-suffix stripping + fuzzy — critical for deu ("(Stufe 10)")
        var exactResult = FallbackProvider.TryLevelSuffixStrippedExact(plain, noApos, _translationsNoDiacritics);
        if (exactResult is not null) { _logger.LogTrace("TCache: level-suffix exact '{Name}' -> '{Result}'", plain, exactResult); return exactResult; }

        var lsResult = FallbackProvider.TryLevelSuffixStrippedMultiChar(plain, noApos, _translationsNoDiacritics, fuzzyDist);
        if (lsResult is not null) { _logger.LogTrace("TCache: level-suffix fuzzy '{Name}' -> '{Result}' (d={D})", plain, lsResult, fuzzyDist); return lsResult; }

        // Space-stripped fallback within level-suffix section
        if (noSpaces != plain)
        {
            foreach (var kvp in _translationsNoDiacritics)
            {
                var strippedKey = FallbackProvider.StripLevelSuffixFromKey(kvp.Key);
                if (strippedKey is null) continue;
                var strippedNoSpaces = FallbackProvider.RemoveAllSpaces(strippedKey);
                if (strippedNoSpaces == strippedKey) continue;
                if (StrComp.AreFewCharsAway(noSpaces, strippedNoSpaces, FallbackProvider.FuzzyMaxDist(noSpaces)))
                {
                    _logger.LogTrace("TCache: level-suffix+space '{Name}' -> '{Result}'", noSpaces, kvp.Value);
                    return kvp.Value;
                }
            }
        }

        _logger.LogTrace("TCache: no match for '{Name}'", plain);
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

    private void RebuildNoDiacriticsMap()
    {
        var map = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _translations)
        {
            var plain = FallbackProvider.RemoveDiacritics(kvp.Key);
            map[plain] = kvp.Value;
            var noApos = FallbackProvider.RemoveApostrophes(plain);
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
