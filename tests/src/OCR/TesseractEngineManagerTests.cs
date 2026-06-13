using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class TesseractEngineManagerTests : IDisposable
{
    private readonly ILogger<TesseractEngineManager> _logger = NullLogger<TesseractEngineManager>.Instance;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_WithLogger_DoesNotThrow()
    {
        using var manager = new TesseractEngineManager(_logger);
        Assert.NotNull(manager);
    }

    [Fact]
    public void GetEngine_NoTraineddata_ThrowsFileNotFound()
    {
        if (TesseractBootstrapper.IsLanguageDataAvailable("eng"))
            return; // traineddata available, skip this negative test

        using var manager = new TesseractEngineManager(_logger);
        var options = new OcrOptions { Language = "eng" };

        Assert.Throws<FileNotFoundException>(() => manager.GetEngine(options));
    }

    [Fact]
    public void GetEngine_ValidPath_ReturnsEngine()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return; // traineddata not available in this environment

        using var manager = new TesseractEngineManager(_logger);
        var options = new OcrOptions { Language = "eng", TesseractDataPath = tessDataPath };

        var engine = manager.GetEngine(options);
        Assert.NotNull(engine);
    }

    [Fact]
    public void GetEngine_SameLanguage_ReturnsSameInstance()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return;

        using var manager = new TesseractEngineManager(_logger);
        var options = new OcrOptions { Language = "eng", TesseractDataPath = tessDataPath };

        var engine1 = manager.GetEngine(options);
        var engine2 = manager.GetEngine(options);

        Assert.Same(engine1, engine2);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var manager = new TesseractEngineManager(_logger);
        manager.Dispose();
        manager.Dispose();
    }

    [Fact]
    public void GetEngine_AfterDispose_RecreatesEngine()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return;

        var manager = new TesseractEngineManager(_logger);
        var options = new OcrOptions { Language = "eng", TesseractDataPath = tessDataPath };
        var engine1 = manager.GetEngine(options);
        Assert.NotNull(engine1);

        manager.Dispose();

        var engine2 = manager.GetEngine(options);
        Assert.NotNull(engine2);
        Assert.NotSame(engine1, engine2);
    }
}
