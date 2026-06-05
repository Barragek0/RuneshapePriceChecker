using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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

    bool IsPoe2WindowForeground { get; }
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
    private sealed record Poe2WindowCandidate(IntPtr WindowHandle, uint ProcessId);
    private readonly IOptionsMonitor<OcrOptions> _options = options;

    private volatile OcrCaptureRegion? _currentCaptureRegion;
    private volatile OcrResolutionProfile? _currentResolutionProfile;
    private volatile string? _currentResolutionKey;
    private volatile WindowCaptureContext? _currentWindowCaptureContext;
    private volatile bool _isPoe2WindowForeground;
    private bool? _lastForegroundState;
    private string? _unsupportedResolutionPopupShownForKey;
    private string? _untestedResolutionKeyShown;
    private bool _fullscreenWarningShown;
    private bool _uiBrightnessWarningShown;

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

            var pollSeconds = Math.Max(2, _options.CurrentValue.ResolutionPollIntervalSeconds);
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

        var configInfo = ReadPoe2ConfigInfo();
        ShowFullscreenWarningPopupIfNeeded(configInfo.IsFullscreen);
        ShowUiBrightnessWarningPopupIfNeeded(configInfo.UiBrightness);

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
        if (!OcrResolutionProfiles.TryGet(resolutionKey, out var profile))
        {
            if (configInfo.ResolutionKey is not null &&
                OcrResolutionProfiles.TryGet(configInfo.ResolutionKey, out _))
            {
                if (!string.Equals(_currentResolutionKey, resolutionKey, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogDebug(
                        "Detected transitional PoE2 client resolution {DetectedResolution}; config resolution is {ConfigResolution}. Waiting for window to stabilize.",
                        resolutionKey,
                        configInfo.ResolutionKey);
                    _currentResolutionKey = resolutionKey;
                    _currentCaptureRegion = null;
                    _currentResolutionProfile = null;
                }

                return;
            }

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
        _untestedResolutionKeyShown = null;

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

        ShowUntestedResolutionPopupIfNeeded(resolutionKey, profile);

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

    private static List<Poe2WindowCandidate> FindPoe2WindowCandidates()
    {
        return Process
            .GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .Where(p => !string.IsNullOrWhiteSpace(p.MainWindowTitle))
            .Where(p => p.MainWindowTitle.Equals("Path of Exile 2", StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var processId = (uint)p.Id;
                return new Poe2WindowCandidate(p.MainWindowHandle, processId);
            })
            .OrderByDescending(p => p.WindowHandle)
            .ToList();
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

    private void ShowUiBrightnessWarningPopupIfNeeded(float? uiBrightness)
    {
        if (_uiBrightnessWarningShown || uiBrightness is null)
            return;

        if (uiBrightness.Value >= 1.01f)
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
            MessageBox.Show(
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
            MessageBox.Show(
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

    private void ShowUntestedResolutionPopupIfNeeded(string resolutionKey, OcrResolutionProfile profile)
    {
        if (profile.Confirmed)
            return;

        if (string.Equals(_untestedResolutionKeyShown, resolutionKey, StringComparison.OrdinalIgnoreCase))
            return;

        _untestedResolutionKeyShown = resolutionKey;

        var message =
            $"This resolution ({resolutionKey}) hasn't been tested with RuneshapePriceChecker yet.{Environment.NewLine}{Environment.NewLine}" +
            "OCR capture may not work correctly and the capture region might need a small manual tweak." +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "If the tool isn't picking up text properly on your setup, there's a step-by-step guide you can follow to tune it:" +
            $"{Environment.NewLine}See ADDING_A_RESOLUTION.md in the tool folder.";

        try
        {
            MessageBox.Show(
                message,
                "RuneshapePriceChecker - Untested Resolution",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1,
                MessageBoxOptions.DefaultDesktopOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to display untested resolution popup.");
        }
    }

    private readonly record struct Poe2ConfigInfo(string? ResolutionKey, bool IsFullscreen, float? UiBrightness);

    private static Poe2ConfigInfo ReadPoe2ConfigInfo()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games",
                "Path of Exile 2",
                "poe2_production_Config.ini");

            if (!File.Exists(configPath))
                return default;

            int? width = null;
            int? height = null;
            var fullscreen = false;
            float? uiBrightness = null;

            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("resolution_width=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(trimmed.AsSpan(trimmed.IndexOf('=') + 1), out var w))
                        width = w;
                }
                else if (trimmed.StartsWith("resolution_height=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(trimmed.AsSpan(trimmed.IndexOf('=') + 1), out var h))
                        height = h;
                }
                else if (trimmed.StartsWith("ui_brigthness=", StringComparison.OrdinalIgnoreCase))
                {
                    if (float.TryParse(
                        trimmed.AsSpan(trimmed.IndexOf('=') + 1),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var brightness))
                    {
                        uiBrightness = brightness;
                    }
                }
                else if (trimmed.Equals("fullscreen=true", StringComparison.OrdinalIgnoreCase))
                {
                    fullscreen = true;
                }
            }

            var resolutionKey = width.HasValue && height.HasValue
                ? $"{width}x{height}"
                : null;

            return new Poe2ConfigInfo(resolutionKey, fullscreen, uiBrightness);
        }
        catch (Exception ex)
        {
            // Config file may be locked during game startup; gracefully fall back.
            System.Diagnostics.Debug.WriteLine($"Failed to read PoE2 config: {ex.Message}");
            return default;
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
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

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

            var builder = new StringBuilder(512);
            _ = GetWindowText(windowHandle, builder, builder.Capacity);
            return builder.ToString();
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
