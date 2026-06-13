using Microsoft.Extensions.Logging;
using RuneshapePriceChecker.Startup;

namespace RuneshapePriceChecker.OCR;

internal sealed class TesseractEngineManager : IDisposable
{
    private readonly ILogger<TesseractEngineManager> _logger;
    private readonly object _engineLock = new();
    private NativeTesseractEngine? _engine;
    private string? _engineLanguage;

    public TesseractEngineManager(ILogger<TesseractEngineManager> logger)
    {
        _logger = logger;
    }

    public NativeTesseractEngine GetEngine(OcrOptions options)
    {
        var language = !string.IsNullOrWhiteSpace(options.Language)
            ? options.Language
            : "eng";

        if (_engine is not null && string.Equals(_engineLanguage, language, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogTrace("Tesseract: reusing existing engine for '{Lang}'", language);
            return _engine;
        }

        if (!TesseractBootstrapper.IsLanguageDataAvailable(language))
        {
            _logger.LogInformation("Tesseract: downloading {Lang} language data...", language);
            TesseractBootstrapper.EnsureLanguageDataAvailableAsync(language, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (!TesseractBootstrapper.IsLanguageDataAvailable(language))
            {
                _logger.LogWarning(
                    "Tesseract: failed to download {Lang} language data, falling back to English", language);
                if (string.Equals(language, "eng", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Tesseract English language data is not available and could not be downloaded.");
                return GetEngine(new OcrOptions
                {
                    Language = "eng",
                    TesseractDataPath = options.TesseractDataPath
                });
            }

            _logger.LogInformation("Tesseract: {Lang} language data download complete", language);
        }

        lock (_engineLock)
        {
            if (_engine is not null && string.Equals(_engineLanguage, language, StringComparison.OrdinalIgnoreCase))
                return _engine;

            if (_engine is not null)
            {
                _logger.LogInformation("Tesseract: switching language from {OldLang} to {NewLang}", _engineLanguage, language);
                _engine.Dispose();
                _engine = null;
            }

            var tessDataPath = !string.IsNullOrWhiteSpace(options.TesseractDataPath)
                ? options.TesseractDataPath
                : TesseractBootstrapper.ResolveTessDataPath();

            if (string.IsNullOrWhiteSpace(tessDataPath))
                throw new FileNotFoundException("Tesseract traineddata directory not found.");

            _logger.LogInformation("Tesseract: creating engine for {Lang}...", language);
            try
            {
                _engine = new NativeTesseractEngine(tessDataPath, language);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Tesseract init failed"))
            {
                _logger.LogWarning(ex, "Tesseract init failed for {Lang}, traineddata may be corrupt. Attempting repair...", language);

                lock (_engineLock)
                {
                    TesseractBootstrapper.RepairLanguageDataAsync(language, CancellationToken.None)
                        .GetAwaiter().GetResult();

                    if (!TesseractBootstrapper.IsLanguageDataAvailable(language))
                        throw new InvalidOperationException(
                            $"Tesseract {language} language data repair failed.", ex);

                    _engine = new NativeTesseractEngine(tessDataPath, language);
                }
            }

            _engineLanguage = language;
            _logger.LogInformation("Tesseract: engine created successfully for {Lang}", language);
            return _engine;
        }
    }

    public void Dispose()
    {
        lock (_engineLock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }

}
