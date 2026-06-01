using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.OCR;

public interface IPoe2WindowResolutionProvider
{
    OcrCaptureRegion? CurrentCaptureRegion { get; }

    OcrResolutionProfile? CurrentResolutionProfile { get; }

    string? CurrentResolutionKey { get; }

    WindowCaptureContext? CurrentWindowCaptureContext { get; }
}

public sealed record WindowCaptureContext(
    IntPtr WindowHandle,
    int ClientX,
    int ClientY,
    int ClientWidth,
    int ClientHeight);

public sealed class Poe2WindowResolutionService(
    IOptionsMonitor<OcrOptions> options,
    ILogger<Poe2WindowResolutionService> logger) : BackgroundService, IPoe2WindowResolutionProvider
{
    private static readonly string[] BrowserProcesses = ["brave", "chrome", "msedge", "firefox", "opera"];
    private readonly IOptionsMonitor<OcrOptions> _options = options;

    private volatile OcrCaptureRegion? _currentCaptureRegion;
    private volatile OcrResolutionProfile? _currentResolutionProfile;
    private volatile string? _currentResolutionKey;
    private volatile WindowCaptureContext? _currentWindowCaptureContext;
    private string? _unsupportedResolutionPopupShownForKey;

    public OcrCaptureRegion? CurrentCaptureRegion => _currentCaptureRegion;

    public OcrResolutionProfile? CurrentResolutionProfile => _currentResolutionProfile;

    public string? CurrentResolutionKey => _currentResolutionKey;

    public WindowCaptureContext? CurrentWindowCaptureContext => _currentWindowCaptureContext;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RefreshWindowState();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh PoE2 window resolution state.");
            }

            var pollSeconds = Math.Max(2, _options.CurrentValue.ResolutionPollIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private void RefreshWindowState()
    {
        var handle = FindPoe2WindowHandle();
        if (handle == IntPtr.Zero)
        {
            _currentWindowCaptureContext = null;
            _currentResolutionProfile = null;
            return;
        }

        if (!NativeMethods.GetClientRect(handle, out var rect))
        {
            _currentWindowCaptureContext = null;
            _currentResolutionProfile = null;
            return;
        }

        var topLeft = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(handle, ref topLeft))
        {
            _currentWindowCaptureContext = null;
            _currentResolutionProfile = null;
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            _currentWindowCaptureContext = null;
            _currentResolutionProfile = null;
            return;
        }

        _currentWindowCaptureContext = new WindowCaptureContext(
            handle,
            topLeft.X,
            topLeft.Y,
            width,
            height);

        var resolutionKey = $"{width}x{height}";
        if (!OcrResolutionProfiles.TryGet(resolutionKey, out var profile))
        {
            if (!string.Equals(_currentResolutionKey, resolutionKey, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Detected PoE2 client resolution {Resolution}, but no OCR profile exists. OCR capture will remain disabled until a profile is added.", resolutionKey);
                _currentResolutionKey = resolutionKey;
                _currentCaptureRegion = null;
                _currentResolutionProfile = null;
                ShowUnsupportedResolutionPopupIfNeeded(resolutionKey);
            }

            return;
        }

        _unsupportedResolutionPopupShownForKey = null;

        if (!TryCreateWindowRelativeRegion(topLeft, width, height, profile, out var region, out var validationError))
        {
            if (!string.Equals(_currentResolutionKey, resolutionKey, StringComparison.OrdinalIgnoreCase) || _currentCaptureRegion is not null)
            {
                logger.LogWarning(
                    "OCR profile for resolution {Resolution} is invalid ({Reason}). OCR capture will remain disabled until the profile is fixed.",
                    resolutionKey,
                    validationError);
            }

            _currentResolutionKey = resolutionKey;
            _currentCaptureRegion = null;
            _currentResolutionProfile = null;
            return;
        }

        if (string.Equals(_currentResolutionKey, resolutionKey, StringComparison.OrdinalIgnoreCase) &&
            Equals(_currentCaptureRegion, region))
        {
            return;
        }

        _currentResolutionKey = resolutionKey;
        _currentCaptureRegion = region;
        _currentResolutionProfile = profile;

        logger.LogInformation(
            "Detected PoE2 client resolution {Resolution}; window origin X={WindowX} Y={WindowY}; offsets X={OffsetX} Y={OffsetY}; OCR region X={X} Y={Y} W={W} H={H}.",
            resolutionKey,
            topLeft.X,
            topLeft.Y,
            profile.CaptureOffsetX,
            profile.CaptureOffsetY,
            region.X,
            region.Y,
            region.Width,
            region.Height);
    }

    private static bool TryCreateWindowRelativeRegion(
        NativeMethods.POINT topLeft,
        int windowWidth,
        int windowHeight,
        OcrResolutionProfile profile,
        out OcrCaptureRegion region,
        out string validationError)
    {
        region = default!;

        if (profile.CaptureWidth <= 0 || profile.CaptureHeight <= 0)
        {
            validationError = "capture size must be positive";
            return false;
        }

        if (profile.CaptureOffsetX < 0 || profile.CaptureOffsetY < 0)
        {
            validationError = "offsets must be non-negative";
            return false;
        }

        if (profile.CaptureOffsetX + profile.CaptureWidth > windowWidth ||
            profile.CaptureOffsetY + profile.CaptureHeight > windowHeight)
        {
            validationError = "offset + size extends outside the PoE2 client area";
            return false;
        }

        region = new OcrCaptureRegion(
            topLeft.X + profile.CaptureOffsetX,
            topLeft.Y + profile.CaptureOffsetY,
            profile.CaptureWidth,
            profile.CaptureHeight);

        validationError = string.Empty;
        return true;
    }

    private static IntPtr FindPoe2WindowHandle()
    {
        var process = Process
            .GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle))
            .Where(p => p.MainWindowTitle.Contains("Path of Exile 2", StringComparison.OrdinalIgnoreCase))
            .Where(p => !BrowserProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(p => p.MainWindowHandle)
            .FirstOrDefault();

        return process?.MainWindowHandle ?? IntPtr.Zero;
    }

    private void ShowUnsupportedResolutionPopupIfNeeded(string detectedResolution)
    {
        if (string.Equals(_unsupportedResolutionPopupShownForKey, detectedResolution, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _unsupportedResolutionPopupShownForKey = detectedResolution;

        var supported = OcrResolutionProfiles.All
            .Select(profile => profile.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var supportedList = supported.Length == 0
            ? "(none configured)"
            : string.Join(Environment.NewLine, supported);

        var message =
            $"Unsupported PoE2 resolution detected: {detectedResolution}{Environment.NewLine}{Environment.NewLine}" +
            "OCR capture is disabled for this resolution." +
            $"{Environment.NewLine}{Environment.NewLine}Supported resolutions:{Environment.NewLine}{supportedList}" +
            $"{Environment.NewLine}{Environment.NewLine}You must run the game in borderless windowed.";

        try
        {
            MessageBox.Show(
                message,
                "RuneshapePriceChecker - Unsupported Resolution",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.DefaultDesktopOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to display unsupported resolution popup.");
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    }
}
