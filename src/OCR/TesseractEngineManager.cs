using Microsoft.Extensions.Logging;
using RuneshapePriceChecker.Configuration;
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

        _logger.LogTrace("Tesseract: acquiring engine lock for '{Lang}'", language);
        lock (_engineLock)
        {
            _logger.LogTrace("Tesseract: inside engine lock");
            if (_engine is not null && string.Equals(_engineLanguage, language, StringComparison.OrdinalIgnoreCase))
                return _engine;

            if (_engine is not null)
            {
                _logger.LogTrace("Tesseract: disposing old engine");
                _engine.Dispose();
                _engine = null;
            }

            if (!TesseractBootstrapper.IsLanguageDataAvailable(language))
            {
                _logger.LogTrace("Tesseract: language data not available, downloading");
                TesseractBootstrapper.EnsureLanguageDataAvailableAsync(language, CancellationToken.None)
                    .GetAwaiter().GetResult();
                _logger.LogTrace("Tesseract: language data download complete");
            }

            var tessDataPath = !string.IsNullOrWhiteSpace(options.TesseractDataPath)
                ? options.TesseractDataPath
                : TesseractBootstrapper.ResolveTessDataPath();

            if (string.IsNullOrWhiteSpace(tessDataPath))
                throw new FileNotFoundException("Tesseract traineddata directory not found.");

            _logger.LogTrace("Tesseract: creating engine with path={Path} lang={Lang}", tessDataPath, language);
            _engine = new NativeTesseractEngine(tessDataPath, language);
            _engineLanguage = language;
            _logger.LogTrace("Tesseract: engine created successfully");
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
