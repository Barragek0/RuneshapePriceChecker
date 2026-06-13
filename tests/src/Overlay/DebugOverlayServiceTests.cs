using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Overlay;
using Xunit;

namespace RuneshapePriceChecker.Tests.Overlay;

public class DebugOverlayServiceTests
{
    [Fact]
    public void Constructor_WithValidDependencies_DoesNotThrow()
    {
        var resolver = new MockWindowResolutionProvider();
        var ocrOpts = Options.Create(new OcrOptions());
        var winOpts = Options.Create(new WindowOptions());
        var logger = new LoggerFactory().CreateLogger<DebugOverlayService>();

        using var service = new DebugOverlayService(
            resolver, new MockOcrOptionsMonitor(ocrOpts), new MockWindowOptionsMonitor(winOpts),
            null!, logger);
        Assert.NotNull(service);
    }

    [Fact]
    public void DebugOverlayService_IsBackgroundService()
    {
        var type = typeof(DebugOverlayService);
        Assert.True(typeof(Microsoft.Extensions.Hosting.BackgroundService).IsAssignableFrom(type));
    }

    private sealed class MockWindowResolutionProvider : IPoe2WindowResolutionProvider
    {
        public OcrCaptureRegion? CurrentCaptureRegion => null;
        public OcrResolutionProfile? CurrentResolutionProfile => null;
        public string? CurrentResolutionKey => null;
        public WindowCaptureContext? CurrentWindowCaptureContext => null;
        public bool IsPoe2WindowForeground => false;
    }

    private sealed class MockOcrOptionsMonitor(IOptions<OcrOptions> options) : IOptionsMonitor<OcrOptions>
    {
        public OcrOptions CurrentValue => options.Value;
        public OcrOptions Get(string? name) => options.Value;
        public IDisposable? OnChange(Action<OcrOptions, string?> listener) => null;
    }

    private sealed class MockWindowOptionsMonitor(IOptions<WindowOptions> options) : IOptionsMonitor<WindowOptions>
    {
        public WindowOptions CurrentValue => options.Value;
        public WindowOptions Get(string? name) => options.Value;
        public IDisposable? OnChange(Action<WindowOptions, string?> listener) => null;
    }
}
