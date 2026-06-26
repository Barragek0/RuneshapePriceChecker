using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Overlay;
using Xunit;

namespace RuneshapePriceChecker.Tests.Overlay;

public class ConsoleOverlayRendererTests
{
    [Fact]
    public void ConsoleOverlayRenderer_ImplementsIOverlayRenderer()
    {
        var type = typeof(PricingOverlayRenderer);
        Assert.True(typeof(IOverlayRenderer).IsAssignableFrom(type));
    }

    public static TheoryData<int, float> ResolutionHeightData =>
        new()
        {
            // 1080p baseline
            { 1080, 1.00f },
            // 4k UHD
            { 2160, 2.00f },
            // 1440p QHD
            { 1440, 1.33f },
            // 1200p
            { 1200, 1.11f },
            // 900p
            { 900, 0.83f },
            // 768p
            { 768, 0.71f },
            // Very small window — clamped to 0.5
            { 200, 0.50f },
        };

    [Theory]
    [MemberData(nameof(ResolutionHeightData))]
    public void ComputeOverlayScale_ScalesByWindowHeight(int clientHeight, float expectedScale)
    {
        var resolver = new FakeResolutionProvider(clientHeight);
        var appOpts = new FakeAppOptionsMonitor(null);

        var scale = PricingOverlayRenderer.ComputeOverlayScale(resolver, appOpts);

        Assert.Equal(expectedScale, scale, precision: 2);
    }

    [Fact]
    public void ComputeOverlayScale_Returns1_WhenNoWindowContext()
    {
        var resolver = new FakeResolutionProvider(null);
        var appOpts = new FakeAppOptionsMonitor(null);

        var scale = PricingOverlayRenderer.ComputeOverlayScale(resolver, appOpts);

        Assert.Equal(1f, scale);
    }

    [Theory]
    [InlineData(2.0f, 2.0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.1f, 0.25f)] // Clamped to min
    [InlineData(5.0f, 4.0f)]  // Clamped to max
    [InlineData(1.0f, 1.0f)]
    public void ComputeOverlayScale_RespectsManualOverride(float overrideValue, float expectedScale)
    {
        var resolver = new FakeResolutionProvider(1080);
        var appOpts = new FakeAppOptionsMonitor(overrideValue);

        var scale = PricingOverlayRenderer.ComputeOverlayScale(resolver, appOpts);

        Assert.Equal(expectedScale, scale, precision: 2);
    }

    private sealed class FakeResolutionProvider : IPoe2WindowResolutionProvider
    {
        private readonly int? _clientHeight;

        public FakeResolutionProvider(int? clientHeight)
        {
            _clientHeight = clientHeight;
        }

        public OcrCaptureRegion? CurrentCaptureRegion => null;
        public OcrResolutionProfile? CurrentResolutionProfile => null;
        public string? CurrentResolutionKey => null;
        public WindowCaptureContext? CurrentWindowCaptureContext =>
            _clientHeight.HasValue
                ? new WindowCaptureContext(IntPtr.Zero, 0, 0, 1920, _clientHeight.Value)
                : null;
        public bool IsPoe2WindowForeground => false;
    }

    private sealed class FakeAppOptionsMonitor(float? overlayScale) : IOptionsMonitor<AppOptions>
    {
        public AppOptions CurrentValue => new() { OverlayScale = overlayScale };
        public AppOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppOptions, string?> listener) => null;
    }
}
