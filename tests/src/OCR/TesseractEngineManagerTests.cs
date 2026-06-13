using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RuneshapePriceChecker.OCR;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class TesseractEngineManagerTests
{
    private readonly ILogger<TesseractEngineManager> _logger = NullLogger<TesseractEngineManager>.Instance;

    [Fact]
    public void Constructor_WithLogger_DoesNotThrow()
    {
        using var manager = new TesseractEngineManager(_logger);
        Assert.NotNull(manager);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var manager = new TesseractEngineManager(_logger);
        manager.Dispose();
        manager.Dispose();
    }
}
