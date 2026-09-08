using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// CA1416: all callers guard with OS build >= 17763
#pragma warning disable CA1416

namespace RuneshapePriceChecker.OCR;

internal sealed class WindowsOcrEngine
{
    private readonly OcrEngine _engine;
    internal string RequestedLanguageTag { get; }
    internal string ActualLanguageTag { get; }
    internal bool IsExactLanguageMatch { get; }
    private static readonly Dictionary<string, string> AppToWindowsLang = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en-US",
        ["fra"] = "fr-FR",
        ["deu"] = "de-DE",
        ["spa"] = "es-ES",
        ["por"] = "pt-BR",
        ["rus"] = "ru-RU",
        ["tha"] = "th-TH",
        ["chi_tra"] = "zh-TW",
        ["kor"] = "ko-KR",
        ["jpn"] = "ja-JP",
    };
    public static string LanguageSettingsUri => "ms-settings:regionlanguage-adddisplaylanguage";
    public static void OpenLanguageSettings()
    {
        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LanguageSettingsUri)
        { UseShellExecute = true });
    }

    public WindowsOcrEngine(string? appLanguage, ILogger? logger = null)
    {
        RequestedLanguageTag = appLanguage is not null && AppToWindowsLang.TryGetValue(appLanguage, out var requested)
            ? requested : "user-profile";
        if (appLanguage is not null && AppToWindowsLang.TryGetValue(appLanguage, out var winLang))
        {
            var selection = TryCreateFromLanguageOrFamily(winLang, appLanguage, logger);
            if (selection is not null)
            {
                _engine = selection.Value.Engine;
                ActualLanguageTag = selection.Value.ActualLanguageTag;
                IsExactLanguageMatch = selection.Value.IsExact;
                return;
            }

            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? throw new InvalidOperationException("Windows OCR engine not available on this system.");
            ActualLanguageTag = "user-profile";
        }
        else
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? throw new InvalidOperationException("No Windows OCR language available.");
            ActualLanguageTag = "user-profile";
        }
    }

    internal static bool IsExactLanguageAvailable(string? appLanguage)
    {
        return appLanguage is not null && AppToWindowsLang.TryGetValue(appLanguage, out var winLang) &&
            OcrEngine.AvailableRecognizerLanguages.Any(language =>
                string.Equals(language.LanguageTag, winLang, StringComparison.OrdinalIgnoreCase));
    }

    private static (OcrEngine Engine, string ActualLanguageTag, bool IsExact)? TryCreateFromLanguageOrFamily(string winLang, string appLang, ILogger? logger)
    {
        // Try the exact regional variant first
        var lang = new Language(winLang);
        var engine = OcrEngine.TryCreateFromLanguage(lang);
        if (engine is not null)
        {
            logger?.LogInformation("Windows OCR language pack for '{AppLang}' ({WinLang}) loaded successfully.",
                appLang, winLang);
            return (engine, winLang, true);
        }

        // Exact variant not installed — try to find any installed pack matching the language family
        var familyPrefix = winLang[..2]; // "en", "fr", "de", etc.
        foreach (var available in OcrEngine.AvailableRecognizerLanguages)
        {
            var tag = available.LanguageTag;
            if (tag.StartsWith(familyPrefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(tag, winLang, StringComparison.OrdinalIgnoreCase))
            {
                engine = OcrEngine.TryCreateFromLanguage(available);
                if (engine is not null)
                {
                    logger?.LogWarning(
                        "Windows OCR language pack for '{AppLang}' ({WinLang}) is not installed. " +
                        "Using '{Fallback}' ({FallbackTag}) as a compatible fallback. " +
                        "Consider installing the exact pack for best accuracy.",
                        appLang, winLang, available.NativeName, tag);
                    return (engine, tag, false);
                }
            }
        }

        // No matching language pack found at all — fall back to user profile languages
        logger?.LogWarning(
            "Windows OCR language pack for '{AppLang}' ({WinLang}) is not installed. " +
            "Falling back to user profile languages. Install the language pack to improve OCR accuracy. " +
            "Press ⊞ Win and search for \"Language & region\" to install it.",
            appLang, winLang);
        var profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        return profileEngine is null ? null : (profileEngine, "user-profile", false);
    }

    public string Recognize(Bitmap bitmap, out int[] wordYPositions, int upscaleFactor, OcrPerfTiming? perf = null)
    {
        long? sw = perf is not null ? OcrPerfTiming.RecordStart(OcrPerfTiming.Slot.Recognize) : null;

        try
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Bmp);
            ms.Position = 0;
            using var stream = ms.AsRandomAccessStream();
            var decoder = BitmapDecoder.CreateAsync(stream).GetAwaiter().GetResult();
            using var softwareBitmap = decoder.GetSoftwareBitmapAsync().GetAwaiter().GetResult();

            var result = _engine.RecognizeAsync(softwareBitmap).GetAwaiter().GetResult();
            var positions = new List<int>(result.Lines.Count);
            var lineTexts = new string[result.Lines.Count];
            for (var i = 0; i < result.Lines.Count; i++)
            {
                var line = result.Lines[i];
                var words = new string[line.Words.Count];
                var ySum = 0.0;
                for (var w = 0; w < line.Words.Count; w++)
                {
                    words[w] = line.Words[w].Text;
                    ySum += line.Words[w].BoundingRect.Y;
                }
                lineTexts[i] = string.Join(" ", words);

                var yTop = line.Words.Count > 0
                    ? (int)(((ySum / line.Words.Count) - 6) / upscaleFactor)
                    : 0;
                positions.Add(yTop);
            }

            wordYPositions = [.. positions];
            return string.Join("\n", lineTexts);
        }
        finally
        {
            if (perf is not null && sw.HasValue)
                perf.RecordEnd(OcrPerfTiming.Slot.Recognize, sw.Value);
        }
    }
}
#pragma warning restore CA1416
