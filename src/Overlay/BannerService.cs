using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;
using System.Drawing;

namespace RuneshapePriceChecker.Overlay;

public sealed class BannerService(
    IPoe2WindowResolutionProvider windowResolutionProvider,
    IOptionsMonitor<AppOptions> appOptions,
    IOptionsMonitor<OcrOptions> ocrOptions,
    ILogger<BannerService> logger)
{
    private Thread? _bannerThread;
    private BannerForm? _bannerForm;
    private readonly object _bannerSync = new();
    private Thread? _boxThread;
    private BannerBoxForm? _boxForm;
    private readonly object _boxSync = new();
    private string _lastBannerMessage = string.Empty;
    private int _lastBannerX;
    private int _lastBannerY;
    private readonly IOptionsMonitor<OcrOptions> _ocrOptions = ocrOptions;

    public void SetBannerMessage(string? message)
    {
        if (appOptions.CurrentValue.AllOverlaysDisabled)
        {
            GetBannerForm()?.SafeHide();
            GetBoxForm()?.SafeHide();
            return;
        }

        if (!appOptions.CurrentValue.Banner)
        {
            GetBoxForm()?.SafeHide();
            return;
        }

        // Start overlay threads early so they're ready before any banner shows.
        // Creating overlays after Lossless Scaling is active can cause cursor disappearance.
        EnsureBannerThreadStarted();
        EnsureBoxThreadStarted();

        if (string.IsNullOrWhiteSpace(message))
        {
            var form = GetBannerForm();
            if (form is { IsDisposed: false, IsHidden: false })
            {
                logger.LogDebug("Banner hidden (no warning items).");
                form.SafeHide();
            }
            GetBoxForm()?.SafeHide();
            _lastBannerMessage = string.Empty;
            return;
        }

        var region = windowResolutionProvider.CurrentCaptureRegion;
        if (region is null)
        {
            GetBannerForm()?.SafeHide();
            GetBoxForm()?.SafeHide();
            _lastBannerMessage = string.Empty;
            return;
        }

        var bannerX = region.X + 2;
        var bannerY = region.Y - (int)(region.Height * 0.22f);
        if (bannerY < 0) bannerY = 0;

        // Skip if banner already visible with same message at same position
        if (string.Equals(message, _lastBannerMessage, StringComparison.Ordinal) && bannerX == _lastBannerX && bannerY == _lastBannerY)
        {
            var banner = GetBannerForm();
            if (banner is { IsDisposed: false, IsHidden: false })
                return;
        }

        _lastBannerMessage = message;
        _lastBannerX = bannerX;
        _lastBannerY = bannerY;

        // Ensure the floating banner thread exists (for lossless scaling compatibility)
        // but don't show it — the orange box now renders the text.
        EnsureBannerThreadStarted();

        // Show orange box overlay above capture region
        var boxHeight = (int)(region.Height * 0.20f);
        var boxX = region.X;
        var boxY = region.Y - boxHeight;
        if (boxY < 0) boxY = 0;
        var box = GetBoxForm();
        if (box is not null)
        {
            // Compute overlay scale the same way as the pricing overlay
            var scaleFactor = PricingOverlayRenderer.ComputeOverlayScale(
                windowResolutionProvider, _ocrOptions.CurrentValue.OverlayScale);
            box.SetScaleFactor(scaleFactor);
            box.ShowOutline = _ocrOptions.CurrentValue.DebugOverlay;
            box.SetMessage(message, region.Height);
            box.SafeShow(boxX, boxY, region.Width, boxHeight);
        }

        // Keep the floating banner hidden — the orange box replaces it visually
        GetBannerForm()?.SafeHide();
    }

    private void EnsureBannerThreadStarted()
    {
        lock (_bannerSync)
        {
            if (_bannerThread is { IsAlive: true }) return;

            _bannerThread = OverlayFormRunner.Start<BannerForm>(
                "RuneshapePriceChecker-Banner",
                _bannerSync,
                f => _bannerForm = f,
                logger,
                "Banner form creation timed out; banner will be unavailable.");
        }
    }

    private BannerForm? GetBannerForm()
    {
        lock (_bannerSync) return _bannerForm;
    }

    private void EnsureBoxThreadStarted()
    {
        lock (_boxSync)
        {
            if (_boxThread is { IsAlive: true }) return;

            _boxThread = OverlayFormRunner.Start<BannerBoxForm>(
                "RuneshapePriceChecker-BannerBox",
                _boxSync,
                f => _boxForm = f,
                logger,
                "Banner box form creation timed out.");
        }
    }

    private BannerBoxForm? GetBoxForm()
    {
        lock (_boxSync) return _boxForm;
    }
}
