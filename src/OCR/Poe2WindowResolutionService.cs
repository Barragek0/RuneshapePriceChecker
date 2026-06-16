using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using StructLinq;

namespace RuneshapePriceChecker.OCR;

public interface IPoe2WindowResolutionProvider
{
    OcrCaptureRegion? CurrentCaptureRegion { get; }

    OcrResolutionProfile? CurrentResolutionProfile { get; }

    string? CurrentResolutionKey { get; }

    WindowCaptureContext? CurrentWindowCaptureContext { get; }

    bool IsPoe2WindowForeground { get; }
}

public sealed record WindowCaptureContext(
    IntPtr WindowHandle,
    int ClientX,
    int ClientY,
    int ClientWidth,
    int ClientHeight);

public sealed class Poe2WindowResolutionService(
    IOptionsMonitor<WindowOptions> windowOptions,
    ILogger<Poe2WindowResolutionService> logger) : BackgroundService, IPoe2WindowResolutionProvider
{
    private sealed record Poe2WindowCandidate(IntPtr WindowHandle, uint ProcessId);
    private readonly IOptionsMonitor<WindowOptions> _windowOptions = windowOptions;

    private volatile OcrCaptureRegion? _currentCaptureRegion;
    private volatile OcrResolutionProfile? _currentResolutionProfile;
    private volatile string? _currentResolutionKey;
    private volatile WindowCaptureContext? _currentWindowCaptureContext;
    private volatile bool _isPoe2WindowForeground;
    private bool? _lastForegroundState;
    private bool _fullscreenWarningShown;
    private bool _uiBrightnessWarningShown;
    private string _lastCustomRegionKey = string.Empty;

    public OcrCaptureRegion? CurrentCaptureRegion => _currentCaptureRegion;

    public OcrResolutionProfile? CurrentResolutionProfile => _currentResolutionProfile;

    public string? CurrentResolutionKey => _currentResolutionKey;

    public WindowCaptureContext? CurrentWindowCaptureContext => _currentWindowCaptureContext;

    public bool IsPoe2WindowForeground => _isPoe2WindowForeground;

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

            var pollSeconds = Math.Max(1, OcrConstants.ResolutionPollIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    private void RefreshWindowState()
    {
        var candidates = FindPoe2WindowCandidates();
        if (candidates.Count == 0)
        {
            _isPoe2WindowForeground = false;
            LogForegroundStateIfChanged(_isPoe2WindowForeground);
            _currentWindowCaptureContext = null;
            _currentResolutionProfile = null;
            return;
        }

        var foregroundHandle = NativeMethods.GetForegroundWindowHandle();
        var foregroundProcessId = NativeMethods.GetProcessIdForWindow(foregroundHandle);
        var foregroundTitle = NativeMethods.GetWindowTitle(foregroundHandle);

        var foregroundCandidate = candidates.FirstOrDefault(c =>
            NativeMethods.AreWindowFamilyRelated(c.WindowHandle, foregroundHandle));

        var isForegroundByWindowFamily = foregroundCandidate is not null;
        var isForegroundByTitle = !string.IsNullOrWhiteSpace(foregroundTitle) &&
            foregroundTitle.Equals("Path of Exile 2", StringComparison.OrdinalIgnoreCase);
        var isForegroundByProcess = foregroundProcessId != 0 &&
            candidates.Any(c => c.ProcessId == foregroundProcessId);

        _isPoe2WindowForeground = isForegroundByWindowFamily || isForegroundByTitle || isForegroundByProcess;
        LogForegroundStateIfChanged(_isPoe2WindowForeground);

        ShowFullscreenWarningPopupIfNeeded(Poe2ConfigFile.IsFullscreen);
        ShowUiBrightnessWarningPopupIfNeeded(Poe2ConfigFile.UiBrightness);

        var handle = foregroundCandidate?.WindowHandle ?? candidates[0].WindowHandle;

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
        var windowOpts = _windowOptions.CurrentValue;
        OcrResolutionProfile? profile;

        if (windowOpts.InitialSetupComplete &&
            windowOpts.CustomOffsetX is { } cx && windowOpts.CustomOffsetY is { } cy &&
            windowOpts.CustomWidth is { } cw && windowOpts.CustomHeight is { } ch)
        {
            profile = new OcrResolutionProfile(cx, cy, cw, ch);
            var regionKey = $"{cx},{cy},{cw},{ch}";
            if (!string.Equals(_lastCustomRegionKey, regionKey, StringComparison.Ordinal))
            {
                _lastCustomRegionKey = regionKey;
                logger.LogDebug("Using custom setup region: X={X} Y={Y} W={W} H={H}", cx, cy, cw, ch);
            }
        }
        else
        {
            profile = ResolveProfile(resolutionKey, width, height);
        }

        if (profile is null)
        {
            if (!string.Equals(_currentResolutionKey, resolutionKey, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Detected PoE2 client resolution {Resolution}, but no OCR profile could be determined.", resolutionKey);
                _currentResolutionKey = resolutionKey;
                _currentCaptureRegion = null;
                _currentResolutionProfile = null;
            }
            return;
        }

        OcrCaptureRegion? region;
        try
        {
            region = OcrCaptureRegionResolver.Resolve(topLeft.X, topLeft.Y, width, height, profile);
        }
        catch (InvalidOperationException ex)
        {
            if (!string.Equals(_currentResolutionKey, resolutionKey, StringComparison.OrdinalIgnoreCase) || _currentCaptureRegion is not null)
            {
                logger.LogWarning(
                    "OCR profile for resolution {Resolution} is invalid ({Reason}). OCR capture will remain disabled until the profile is fixed.",
                    resolutionKey,
                    ex.Message);
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

        var matchType = windowOpts.InitialSetupComplete ? "custom" :
            OcrResolutionProfiles.TryGet(resolutionKey, out _) ? "exact" : "interpolated";
        logger.LogInformation(
            "Detected PoE2 client resolution {Resolution} ({MatchType}); window origin X={WindowX} Y={WindowY}; offsets X={OffsetX} Y={OffsetY}; OCR region X={X} Y={Y} W={W} H={H}.",
            resolutionKey,
            matchType,
            topLeft.X,
            topLeft.Y,
            profile.CaptureOffsetX,
            profile.CaptureOffsetY,
            region.X,
            region.Y,
            region.Width,
            region.Height);
    }

    private static OcrResolutionProfile? ResolveProfile(string resolutionKey, int width, int height)
    {
        return OcrResolutionProfiles.TryGet(resolutionKey, out var exact)
            ? exact
            : OcrResolutionProfiles.Interpolate(width, height);
    }

    private void LogForegroundStateIfChanged(bool isForeground)
    {
        if (_lastForegroundState == isForeground)
        {
            return;
        }

        _lastForegroundState = isForeground;
        if (isForeground)
        {
            logger.LogInformation("PoE2 foreground detected; OCR scanning is active.");
        }
        else
        {
            logger.LogInformation("PoE2 is not foreground; OCR scanning is paused.");
        }
    }

    private static List<Poe2WindowCandidate> FindPoe2WindowCandidates()
    {
        return
        [
            .. Process.GetProcesses()
                .ToStructEnumerable()
                .Where(p => p.MainWindowHandle != IntPtr.Zero, _ => _)
                .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle), _ => _)
                .Where(p => p.MainWindowTitle.Equals("Path of Exile 2", StringComparison.OrdinalIgnoreCase), _ => _)
                .Select(p =>
                {
                    var processId = (uint)p.Id;
                    return new Poe2WindowCandidate(p.MainWindowHandle, processId);
                }, _ => _)
                .ToArray()
        ];
    }

    private void ShowUiBrightnessWarningPopupIfNeeded(float? uiBrightness)
    {
        if (_uiBrightnessWarningShown || uiBrightness is null)
            return;

        // In-game slider for this value works in a weird way.
        // It gets saved between 0.2 (-5.0 ingame) and 5 (+5.0 ingame) in the config.
        // Slider has a -0.0 and +0.0 ingame value on the slider, which means 0.98 - 1 in the config.
        if (uiBrightness.Value >= 0.98f)
            return;

        _uiBrightnessWarningShown = true;

        var message =
            "Your in-game UI Brightness setting, under Graphics, must be set above -0.8, " +
            "with it ideally being at least 0.0." +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "If you set it below 0.0, it's more likely that it will incorrectly match the text " +
            "with the wrong items, and if you set it below -0.8, it may not be able to detect " +
            "the text on the interface at all.";

        try
        {
            _ = MessageBox.Show(
                message,
                "RuneshapePriceChecker - UI Brightness Too Low",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.DefaultDesktopOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to display UI brightness warning popup.");
        }
    }

    private void ShowFullscreenWarningPopupIfNeeded(bool isFullscreen)
    {
        if (!isFullscreen || _fullscreenWarningShown)
            return;

        _fullscreenWarningShown = true;

        var message =
            "PoE2 is running in exclusive fullscreen mode, which prevents OCR screen capture." +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "Please switch to Borderless Windowed or Windowed mode in the game's Display settings.";

        try
        {
            _ = MessageBox.Show(
                message,
                "RuneshapePriceChecker - Fullscreen Detected",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.DefaultDesktopOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to display fullscreen warning popup.");
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        public static IntPtr GetForegroundWindowHandle()
        {
            return GetForegroundWindow();
        }

        public static uint GetProcessIdForWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return 0;
            }

            _ = GetWindowThreadProcessId(windowHandle, out var processId);
            return processId;
        }

        public static string GetWindowTitle(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return string.Empty;
            }

            var buffer = new char[512];
            _ = GetWindowText(windowHandle, buffer, buffer.Length);
            return new string(buffer, 0, Array.IndexOf(buffer, '\0'));
        }

        public static bool AreWindowFamilyRelated(IntPtr candidateWindowHandle, IntPtr foregroundWindowHandle)
        {
            if (candidateWindowHandle == IntPtr.Zero || foregroundWindowHandle == IntPtr.Zero)
            {
                return false;
            }

            if (candidateWindowHandle == foregroundWindowHandle)
            {
                return true;
            }

            if (IsChild(candidateWindowHandle, foregroundWindowHandle) || IsChild(foregroundWindowHandle, candidateWindowHandle))
            {
                return true;
            }

            const uint gaRoot = 2;
            var candidateRoot = GetAncestor(candidateWindowHandle, gaRoot);
            var foregroundRoot = GetAncestor(foregroundWindowHandle, gaRoot);

            return candidateRoot != IntPtr.Zero && candidateRoot == foregroundRoot;
        }
    }
}
