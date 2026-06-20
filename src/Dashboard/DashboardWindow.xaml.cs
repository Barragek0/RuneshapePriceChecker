using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed partial class DashboardWindow : Window
{
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GwHwndfirst = 0;
    private IntPtr _windowHandle;
    private readonly DashboardLogSink _sink;
    private readonly DashboardViewModel _vm;
    private readonly double _baseWindowWidth = 520;
    private readonly double _baseWindowHeight = 691;
    private const double DebugPanelWidth = 440;
    private bool _loading;
    private bool _setupPending;
    private bool _settingsVisible;
    private bool _debugPanelOpen;
    private DispatcherTimer? _moveResizeTimer;
    private DateTime _statusLockedUntil = DateTime.MinValue;
    private readonly DebugMetricsCollector? _metrics;
    private DispatcherTimer? _debugTimer;
    private DispatcherTimer? _languagePackTimer;
    private DispatcherTimer? _alwaysOnTopTimer;
    private DispatcherTimer? _setupPollTimer;
    private string? _pendingLanguageAppTag;

    public ObservableCollection<LogEntryViewModel> LogEntries => _vm.LogEntries;

    public event Action? ChangelogShown;
    public event Action? ChangelogDismissed;

    internal bool IsChangelogVisible { get; private set; }
    internal static volatile bool IsUpdating;

    public DashboardWindow(DashboardLogSink sink, DebugMetricsCollector? metrics = null)
    {
        _sink = sink;
        _metrics = metrics;
        var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
        _vm = new DashboardViewModel(configPath);
        DataContext = this;
        InitializeComponent();
        Deactivated += Window_Deactivated;
        Opacity = 0;
        InitializeScale();
        LogList.DataContext = this;

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.2.2";
        var plusIdx = version.IndexOf('+');
        if (plusIdx >= 0) version = version[..plusIdx];
        VersionRun.Text = $"v{version}";

        _sink.OnLogEntry += entry =>
        {
            Dispatcher.Invoke(() =>
            {
                _vm.OnLogEntry(entry);
                if (!entry.Message.Contains("Windows OCR language pack", StringComparison.OrdinalIgnoreCase))
                    return;

                if (entry.Message.Contains("loaded successfully", StringComparison.OrdinalIgnoreCase))
                {
                    OcrLanguageWarning.Visibility = Visibility.Collapsed;
                    StopLanguagePackWatchdog();
                }
                else if (entry.Message.Contains("not installed", StringComparison.OrdinalIgnoreCase))
                {
                    OcrLanguageWarning.Visibility = Visibility.Visible;
                    // Extract Windows language tag from the first "(xx-XX)" in the message
                    var parenStart = entry.Message.IndexOf('(');
                    var parenEnd = entry.Message.IndexOf(')', parenStart + 1);
                    if (parenStart >= 0 && parenEnd > parenStart)
                    {
                        var winTag = entry.Message[(parenStart + 1)..parenEnd];
                        try
                        {
                            var culture = new CultureInfo(winTag);
                            WarningLanguageName.Text = culture.DisplayName;
                        }
                        catch
                        {
                            WarningLanguageName.Text = winTag;
                        }
                    }
                    // Store the app language code (e.g. "por") from the tag before the first "("
                    // so the watchdog can check when the pack is installed.
                    var langStart = entry.Message.IndexOf('\'');
                    var langEnd = langStart > 0 ? entry.Message.IndexOf('\'', langStart + 1) : -1;
                    _pendingLanguageAppTag = langStart > 0 && langEnd > langStart
                        ? entry.Message[(langStart + 1)..langEnd]
                        : null;
                    StartLanguagePackWatchdog();
                }
            });
        };

        foreach (var entry in _sink.Snapshot().Reverse())
            _vm.OnLogEntry(entry);

        PopulateOcrBackendCombo();
        PopulatePricingSourceCombo();
        PopulateLogLevelCombo();
        _vm.LoadSettings();
        SyncUiFromViewModel();
        RestoreDebugPanelState();
        _ = LoadLeaguesAsync();

        CheckPendingChangelog();

        if (HasArg("--App:ShowChangelog=true")) Loaded += (_, _) => ShowChangelogPreview();
        if (HasArg("--App:ForceUpdateAvailable=true") || _vm.ConfigHasFlag("App", "ForceUpdateAvailable"))
            ShowUpdateButton();

        if (HasArg("--App:AutoApplyUpdate=true") || _vm.ConfigHasFlag("App", "AutoApplyUpdate"))
        {
            ShowUpdateButton();
            Loaded += async (_, _) =>
            {
                for (var i = 0; i < 30; i++)
                {
                    if (_vm.OnUpdateTriggered is not null) break;
                    await Task.Delay(500);
                }
                if (_vm.OnUpdateTriggered is not null)
                {
                    // Clear the flag so it doesn't trigger again after the update
                    _vm.SetConfigFlag("App", "AutoApplyUpdate", false);
                    Dispatcher.Invoke(() => Update_Click(this, new RoutedEventArgs()));
                }
            };
        }

        if (HasArg("--App:TestMode=true"))
            TestModeIndicator.Visibility = Visibility.Visible;

        if (HasArg("--App:SuppressActivation=true"))
            _suppressActivation = true;

        if (HasArg("--App:Headless=true"))
            _headless = true;
    }

    private readonly bool _suppressActivation;
    private readonly bool _headless;

    private static bool HasArg(string arg)
    {
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (string.Equals(a, arg, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void CheckPendingChangelog()
    {
        var pendingVersion = _vm.TryGetPendingChangelogVersion();
        if (pendingVersion is not null && UpdateProgressPanel.Visibility != Visibility.Visible)
        {
            _vm.MarkChangelogShown();
            _ = FetchAndShowChangelogAsync(pendingVersion);
            return;
        }

        if (!_vm.HasChangelogSection())
        {
            Loaded += (_, _) =>
            {
                _ = Dispatcher.BeginInvoke(new Action(async () =>
                {
                    for (var i = 0; i < 20; i++)
                    {
                        await Task.Delay(1000);
                        if (UpdateProgressPanel.Visibility == Visibility.Visible)
                            continue;
                        pendingVersion = _vm.TryGetPendingChangelogVersion();
                        if (pendingVersion is not null)
                        {
                            _vm.MarkChangelogShown();
                            await FetchAndShowChangelogAsync(pendingVersion);
                            return;
                        }
                    }
                }), DispatcherPriority.Background);
            };
        }
    }

    private void ShowChangelogPreview()
    {
        var changelogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "changelog-v0.2.0.md"));
        if (!File.Exists(changelogPath)) changelogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tests", "changelog-v0.2.0.md"));
        if (!File.Exists(changelogPath))
        {
            LogError("Changelog preview file not found");
            return;
        }

        var body = File.ReadAllText(changelogPath);
        ShowChangelog("0.2.0", body);
    }

    private void ShowChangelog(string version, string body)
    {
        Dispatcher.Invoke(() =>
        {
            IsChangelogVisible = true;
            var title = $"## v{version} Changelog\n\n";
            ChangelogViewer.Document = MarkdownRenderer.Render(title + body);
            RefreshContentArea();
        });
        ChangelogShown?.Invoke();
    }

    private void ChangelogClose_Click(object sender, RoutedEventArgs e)
    {
        IsChangelogVisible = false;
        RefreshContentArea();
        ChangelogDismissed?.Invoke();
    }

    private void SyncUiFromViewModel()
    {
        _loading = true;
        for (var i = 0; i < LogLevelCombo.Items.Count; i++)
        {
            if (LogLevelCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, _vm.LogLevel, StringComparison.OrdinalIgnoreCase))
            { LogLevelCombo.SelectedIndex = i; break; }
        }
        for (var i = 0; i < PricingSourceCombo.Items.Count; i++)
        {
            if (string.Equals(PricingSourceCombo.Items[i] as string, _vm.PricingSource, StringComparison.OrdinalIgnoreCase)) { PricingSourceCombo.SelectedIndex = i; break; }
        }
        var isExalt = string.Equals(_vm.DisplayCurrency, "exalt", StringComparison.OrdinalIgnoreCase);
        CurrencyChaosCheck.IsChecked = !isExalt;
        CurrencyExaltCheck.IsChecked = isExalt;
        RedThresholdBox.Text = _vm.RedThreshold.ToString(CultureInfo.InvariantCulture);
        OrangeThresholdBox.Text = _vm.OrangeThreshold.ToString(CultureInfo.InvariantCulture);
        GreenThresholdBox.Text = _vm.GreenThreshold.ToString(CultureInfo.InvariantCulture);
        DebugOverlayCheck.IsChecked = _vm.DebugOverlay;
        HideDebugOverlayCheck.IsChecked = _vm.HideDebugOverlayWhenInterfaceNotDetected;
        SaveDebugImagesCheck.IsChecked = _vm.SaveDebugImages;
        AutoUpdateCheck.IsChecked = _vm.AutoUpdate;
        BringToForegroundCheck.IsChecked = _vm.BringToForeground;
        AlwaysOnTopCheck.IsChecked = _vm.AlwaysOnTop;
        if (_vm.AlwaysOnTop)
        {
            ForceTopmost();
            StartAlwaysOnTopTimer();
        }
        else
        {
            Topmost = false;
            StopAlwaysOnTopTimer();
        }
        // Language is auto-detected from game config
        for (var i = 0; i < OcrBackendCombo.Items.Count; i++)
        {
            if (string.Equals((OcrBackendCombo.Items[i] as string)?.ToLowerInvariant(), _vm.OcrBackend, StringComparison.OrdinalIgnoreCase)) { OcrBackendCombo.SelectedIndex = i; break; }
        }
        _loading = false;
        UpdateOcrBackendWarning();
        HideDebugOverlayCheck.Visibility = _vm.DebugOverlay ? Visibility.Visible : Visibility.Collapsed;
        SaveDebugImagesCheck.Visibility = _vm.DebugOverlay ? Visibility.Visible : Visibility.Collapsed;
        UpdateBringToForegroundVisibility();
        ValidateThresholds();
    }

    private void SyncViewModelFromUi()
    {
        _vm.LogLevel = (LogLevelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Information";
        _vm.PricingSource = PricingSourceCombo.SelectedItem as string ?? "poe2scout";
        _vm.CurrentLeague = LeagueCombo.SelectedItem as string ?? "";
        _vm.DisplayCurrency = CurrencyExaltCheck.IsChecked == true ? "exalt" : "chaos";
        _ = decimal.TryParse(RedThresholdBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var red); _vm.RedThreshold = red;
        _ = decimal.TryParse(OrangeThresholdBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var orange); _vm.OrangeThreshold = orange;
        _ = decimal.TryParse(GreenThresholdBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var green); _vm.GreenThreshold = green;
        _vm.DebugOverlay = DebugOverlayCheck.IsChecked == true;
        _vm.HideDebugOverlayWhenInterfaceNotDetected = HideDebugOverlayCheck.IsChecked == true;
        _vm.SaveDebugImages = SaveDebugImagesCheck.IsChecked == true;
        // Language is auto-detected from game config - leave _vm.OcrLanguage as-is
        _vm.OcrBackend = (OcrBackendCombo.SelectedItem as string)?.ToLowerInvariant() ?? "windows";
        _vm.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        _vm.BringToForeground = BringToForegroundCheck.IsChecked == true;
        _vm.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        if (_vm.AlwaysOnTop)
            ForceTopmost();
        else
            Topmost = false;
    }

    private void InitializeScale()
    {
        var h = SystemParameters.PrimaryScreenHeight;
        var scale = Math.Clamp(h / 1080.0, 1, 1.5);
        Width = _baseWindowWidth * scale;
        Height = _baseWindowHeight * scale;
    }

    public void SetStatus(string text, string color = "green")
    {
        if (color != "red" && DateTime.UtcNow < _statusLockedUntil)
            return;

        Dispatcher.Invoke(() =>
        {
            StatusLabel.Text = $"● {text}";
            StatusLabel.Foreground = color switch
            {
                "amber" => (Brush)FindResource("AmberBrush"),
                "red" => (Brush)FindResource("RedBrush"),
                _ => (Brush)FindResource("GreenBrush")
            };
        });

        if (color == "red")
            _statusLockedUntil = DateTime.UtcNow.AddSeconds(3);
    }

    public void LogError(string message)
    {
        _sink.Emit(message, "red");
        if (!IsLogVisibleToUser)
            SetStatus(message, "red");
    }

    private bool IsLogVisibleToUser =>
        SetupPromptSection.Visibility != Visibility.Visible &&
        !IsChangelogVisible &&
        !_settingsVisible;

    public void SetOnSetupContinue(Action callback)
    {
        _vm.OnSetupContinue = callback;
    }

    public void ShowSetupPrompt()
    {
        Dispatcher.Invoke(() =>
        {
            _setupPending = true;
            SetupContinueButton.IsEnabled = false;
            StartSetupPollTimer();
            RefreshContentArea();
        });
    }

    public void HideSetupPrompt()
    {
        Dispatcher.Invoke(() =>
        {
            _setupPending = false;
            StopSetupPollTimer();
            RefreshContentArea();
        });
    }

    private void StartSetupPollTimer()
    {
        StopSetupPollTimer();
        _setupPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _setupPollTimer.Tick += OnSetupPollTick;
        _setupPollTimer.Start();
    }

    private void StopSetupPollTimer()
    {
        if (_setupPollTimer is null) return;
        _setupPollTimer.Stop();
        _setupPollTimer.Tick -= OnSetupPollTick;
        _setupPollTimer = null;
    }

    private void OnSetupPollTick(object? sender, EventArgs e)
    {
        if (!_setupPending) return;

        var poe2Running = Process.GetProcesses()
            .Any(p =>
            {
                try { return p.MainWindowTitle.Contains("Path of Exile 2", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });

        if (poe2Running)
        {
            SetupContinueButton.IsEnabled = true;
            StopSetupPollTimer();
        }
    }

    private void RefreshContentArea()
    {
        ChangelogSection.Visibility = Visibility.Collapsed;
        SetupPromptSection.Visibility = Visibility.Collapsed;
        SettingsSection.Visibility = Visibility.Collapsed;
        LogSection.Visibility = Visibility.Collapsed;

        if (_debugPanelOpen)
        {
            // Debug panel stays visible; only toggle left side content
            if (IsChangelogVisible)
            {
                ChangelogSection.Visibility = Visibility.Visible;
                return;
            }

            if (_setupPending)
            {
                SetupPromptSection.Visibility = Visibility.Visible;
                return;
            }

            if (_settingsVisible)
            {
                SettingsSection.Visibility = Visibility.Visible;
                return;
            }

            LogSection.Visibility = Visibility.Visible;
            return;
        }

        StopDebugTimer();

        if (IsChangelogVisible)
        {
            ChangelogSection.Visibility = Visibility.Visible;
            return;
        }

        if (_setupPending)
        {
            SetupPromptSection.Visibility = Visibility.Visible;
            return;
        }

        if (_settingsVisible)
        {
            SettingsSection.Visibility = Visibility.Visible;
            return;
        }

        LogSection.Visibility = Visibility.Visible;
        UpdateButtonHighlights();
    }

    private void SetupContinue_Click(object sender, RoutedEventArgs e)
    {
        _vm.OnSetupContinue?.Invoke();
    }

    private void AlwaysOnTop_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var isOn = AlwaysOnTopCheck.IsChecked == true;
        _vm.AlwaysOnTop = isOn;
        if (isOn)
            ForceTopmost();
        else
            Topmost = false;
        UpdateBringToForegroundVisibility();

        if (isOn)
            StartAlwaysOnTopTimer();
        else
            StopAlwaysOnTopTimer();
    }

    private void UpdateBringToForegroundVisibility()
    {
        BringToForegroundCheck.Visibility = AlwaysOnTopCheck.IsChecked == true
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_vm.AlwaysOnTop)
            ForceTopmost();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private static readonly SolidColorBrush HeaderFooterHover = new(Color.FromRgb(0x2A, 0x2E, 0x38));

    private void Section_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = HeaderFooterHover;
    }

    private void Section_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2E));
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        RestoreWindowPosition();
        _windowHandle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_windowHandle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCCALCSIZE = 0x0083;
        const int WM_NCHITTEST = 0x0084;
        const int WM_NCACTIVATE = 0x0086;
        const int HTCLIENT = 1;

        if (msg == WM_NCACTIVATE)
        {
            handled = true;
            return 1;
        }

        if (msg == WM_NCCALCSIZE)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_NCHITTEST)
        {
            var l = lParam.ToInt64();
            var point = new Point(
                (short)(l & 0xFFFF),
                (short)((l >> 16) & 0xFFFF));
            point = PointFromScreen(point);

            if (Width <= 0 || Height <= 0)
            {
                handled = true;
                return HTCLIENT;
            }

            const int border = 6;
            const int HTTOP = 12;
            const int HTBOTTOM = 15;

            var atTop = point.Y <= border;
            var atBottom = point.Y >= Height - border;

            // Width is fixed (500px), so only allow vertical resize via top/bottom edges
            if (atTop) { handled = true; return HTTOP; }
            if (atBottom) { handled = true; return HTBOTTOM; }
            handled = true;
            return HTCLIENT;
        }

        return IntPtr.Zero;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsVisible)
            UnlockStatus();
        ToggleSettings();
    }

    private async void ViewChangelog_Click(object sender, RoutedEventArgs e)
    {
        StartChangelogSpinner();
        try
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "";
            var plusIdx = version.IndexOf('+');
            if (plusIdx >= 0) version = version[..plusIdx];
            if (string.IsNullOrWhiteSpace(version))
            {
                LogError("Cannot determine current version.");
                return;
            }

            await FetchAndShowChangelogAsync(version);
        }
        finally
        {
            StopChangelogSpinner();
        }
    }

    private async Task FetchAndShowChangelogAsync(string version)
    {
        try
        {
            // Use pre-populated changelog body from config if available (avoids network call)
            var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
            if (File.Exists(configPath))
            {
                var cfgJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath));
                var cfgBody = cfgJson?["Changelog"]?["Body"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(cfgBody))
                {
                    ShowChangelog(version, cfgBody);
                    return;
                }
            }

            var owner = "Barragek0";
            var repo = "RuneshapePriceChecker";
            var apiBase = "https://api.github.com";
            if (File.Exists(configPath))
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath));
                owner = json?["Update"]?["GitHubRepoOwner"]?.GetValue<string>() ?? owner;
                repo = json?["Update"]?["GitHubRepoName"]?.GetValue<string>() ?? repo;
                apiBase = json?["Update"]?["GitHubApiBaseUrl"]?.GetValue<string>() ?? apiBase;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("RuneshapePriceChecker", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var body = await FetchReleaseBodyAsync(http, $"{apiBase.TrimEnd('/')}/repos/{owner}/{repo}/releases/tags/v{version}");
            body ??= await FetchReleaseBodyAsync(http, $"{apiBase.TrimEnd('/')}/repos/{owner}/{repo}/releases/tags/{version}");

            if (body is not null) ShowChangelog(version, body);
            else
            {
                LogError($"Error fetching changelog for v{version}");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error fetching changelog for v{version}: {ex.Message}");
        }
    }

    private static async Task<string?> FetchReleaseBodyAsync(HttpClient http, string url)
    {
        var response = await http.GetAsync(url).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        return root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == System.Text.Json.JsonValueKind.String
            ? bodyProp.GetString()
            : null;
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.OnUpdateTriggered is null) return;
        ShowUpdateOverlay();
        SetUpdateProgress(0);
        _vm.OnUpdateTriggered(new Progress<int>(SetUpdateProgress));
    }

    private static readonly SolidColorBrush UpdateBadgeHoverBg = new(Color.FromRgb(0x14, 0x4A, 0x30));

    private void UpdateBadge_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdateBadge.Background = UpdateBadgeHoverBg;
    }

    private void UpdateBadge_MouseLeave(object sender, MouseEventArgs e)
    {
        UpdateBadge.Background = (Brush)FindResource("DarkGreenBgBrush");
    }

    public void SetUpdateTrigger(Action<IProgress<int>> trigger)
    {
        _vm.OnUpdateTriggered = trigger;
    }

    public void ShowUpdateButton()
    {
        Dispatcher.Invoke(() =>
        {
            if (UpdateProgressPanel.Visibility == Visibility.Visible) return;
            UpdateBadge.Visibility = Visibility.Visible;
        });
    }

    public void HideUpdateButton()
    {
        _ = Dispatcher.Invoke(() => UpdateBadge.Visibility = Visibility.Collapsed);
    }

    public void SetReRunSetupTrigger(Action trigger)
    {
        _vm.OnReRunSetup = trigger;
    }

    public void ShowUpdateOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            UpdateBadge.Visibility = Visibility.Collapsed;
            UpdateProgressPanel.Visibility = Visibility.Visible;
            StartSpinner();
        });
    }

    public void HideUpdateOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            StopSpinner();
        });
    }

    public void SetUpdateProgress(int percent)
    {
        Dispatcher.Invoke(() =>
        {
            var label = "Updating";
            UpdateProgressText.Text = $"{label} {Math.Clamp(percent, 0, 100)}%";
        });
    }

    private void StartSpinner()
    {
        var animation = new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1.2)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void StopSpinner()
    {
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        SpinnerRotate.Angle = 0;
    }

    private void StartChangelogSpinner()
    {
        ChangelogSpinner.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        ChangelogSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void StopChangelogSpinner()
    {
        ChangelogSpinner.Visibility = Visibility.Collapsed;
        ChangelogSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        ChangelogSpinnerRotate.Angle = 0;
    }

    private void ToggleSettings()
    {
        if (_settingsVisible)
        {
            SyncViewModelFromUi();
            _ = _vm.SaveSettings();
        }
        if (!_settingsVisible) IsChangelogVisible = false;
        _settingsVisible = !_settingsVisible;
        RefreshContentArea();
        UpdateButtonHighlights();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void InstallLanguageLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        _ = Process.Start(new ProcessStartInfo(e.Uri.ToString())
        { UseShellExecute = true });
        e.Handled = true;
    }

    private void SwitchToTesseractLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        _vm.OcrBackend = "tesseract";
        _ = _vm.SaveSettings();
        OcrLanguageWarning.Visibility = Visibility.Collapsed;
        StopLanguagePackWatchdog();
        e.Handled = true;
    }

    private void ReRunSetup_Click(object sender, RoutedEventArgs e) { ToggleSettings(); _vm.OnReRunSetup?.Invoke(); }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void Window_ContentRendered(object sender, EventArgs e)
    {
        FadeIn();
        UpdateButtonHighlights();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (IsUpdating || File.Exists(Path.Combine(AppContext.BaseDirectory, ".update-pending")))
        {
            e.Cancel = true;
            return;
        }
        if (_debugPanelOpen)
            _vm.RememberDebugPanel = true;
        else
            _vm.RememberDebugPanel = false;
        _vm.SaveRememberDebugPanel();
        SyncViewModelFromUi();
        _ = _vm.SaveSettings();
        StopAlwaysOnTopTimer();
        SaveWindowPosition();
    }

    private void FadeIn()
    {
        if (_headless)
            return;

        if (_suppressActivation || !_vm.BringToForeground)
        {
            Opacity = 1;
            return;
        }

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) => BringToFront();
        BeginAnimation(OpacityProperty, anim);
    }

    internal void BringToFront()
    {
        if (_suppressActivation) return;

        if (_vm.BringToForeground)
        {
            ForceTopmost();
            _ = Activate();
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_vm.AlwaysOnTop)
                ForceTopmost();
            else
                Topmost = false;
        }), DispatcherPriority.Background);
    }

    private void LogBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLogVisibleToUser)
        {
            _settingsVisible = false;
            IsChangelogVisible = false;
            RefreshContentArea();
        }
        UpdateButtonHighlights();
    }

    private void Debug_Click(object sender, RoutedEventArgs e)
    {
        ToggleDebug();
    }

    private void UpdateButtonHighlights()
    {
        LogBtn.Background = IsLogVisibleToUser
            ? new SolidColorBrush(Color.FromArgb(0x33, 0x58, 0xD9, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        SettingsBtn.Background = _settingsVisible
            ? new SolidColorBrush(Color.FromArgb(0x33, 0x58, 0xD9, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        DebugBtn.Background = _debugPanelOpen
            ? new SolidColorBrush(Color.FromArgb(0x33, 0x58, 0xD9, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    }

    private void ToggleDebug()
    {
        if (_debugPanelOpen)
        {
            // Close: restore normal layout
            _debugPanelOpen = false;
            _vm.RememberDebugPanel = false;
            _vm.SaveRememberDebugPanel();
            DebugDivider.Visibility = Visibility.Collapsed;
            DebugPanelContainer.Visibility = Visibility.Collapsed;
            DebugColumn.Width = new GridLength(0);
            UpdateSectionCornerRadii();
            StopDebugTimer();
            RestoreWindowWidth();
            RefreshContentArea();
        }
        else
        {
            // Open: expand right for debug panel, left side follows current state
            _debugPanelOpen = true;
            _vm.RememberDebugPanel = true;
            _vm.SaveRememberDebugPanel();
            ExpandWindowForDebug();
            DebugColumn.Width = new GridLength(1, GridUnitType.Star);
            DebugDivider.Visibility = Visibility.Visible;
            DebugPanelContainer.Visibility = Visibility.Visible;
            UpdateSectionCornerRadii();
            RefreshContentArea();
            RefreshDebugMetrics();
            StartDebugTimer();
        }
        UpdateButtonHighlights();
    }

    private void StartDebugTimer()
    {
        StopDebugTimer();
        _debugTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debugTimer.Tick += (_, _) => RefreshDebugMetrics();
        _debugTimer.Start();
    }

    private void StopDebugTimer()
    {
        if (_debugTimer is null) return;
        _debugTimer.Stop();
        _debugTimer = null;
    }

    private void StartAlwaysOnTopTimer()
    {
        StopAlwaysOnTopTimer();
        _alwaysOnTopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _alwaysOnTopTimer.Tick += (_, _) =>
        {
            if (_vm.AlwaysOnTop)
                ForceTopmost();
        };
        _alwaysOnTopTimer.Start();
    }

    private void StopAlwaysOnTopTimer()
    {
        if (_alwaysOnTopTimer is null) return;
        _alwaysOnTopTimer.Stop();
        _alwaysOnTopTimer = null;
    }

    private void ForceTopmost()
    {
        // Belt-and-suspenders: WPF Topmost + P/Invoke SetWindowPos with HWND_TOPMOST.
        // Skip SetWindowPos if we're already the topmost window in the Z-order
        // to avoid unnecessary DWM interactions. 50ms timer is safe (SetWindowPos is a
        // fast kernel32→win32u syscall, ~0.001ms), but the check saves any concern.
        Topmost = true;
        if (_windowHandle != IntPtr.Zero
            && GetWindow(IntPtr.Zero, GwHwndfirst) != _windowHandle)
        {
            _ = SetWindowPos(_windowHandle, HwndTopmost, 0, 0, 0, 0,
                SwpNosize | SwpNomove | SwpNoactivate);
        }
    }

    private void ExpandWindowForDebug()
    {
        var scale = Math.Clamp(SystemParameters.PrimaryScreenHeight / 1080.0, 1, 1.5);
        Width = (_baseWindowWidth + DebugPanelWidth) * scale;
        MaxWidth = (int)((_baseWindowWidth + DebugPanelWidth) * 1.1);
    }

    private void RestoreDebugPanelState()
    {
        if (!_vm.RememberDebugPanel || _debugPanelOpen) return;
        ToggleDebug();
    }

    private void UpdateSectionCornerRadii()
    {
        var leftCorners = _debugPanelOpen
            ? new CornerRadius(0, 0, 10, 0)  // bottom-left only, square at separator
            : new CornerRadius(0, 0, 10, 10); // both bottom corners
        LogSection.CornerRadius = leftCorners;
        ChangelogSection.CornerRadius = leftCorners;
        SetupPromptSection.CornerRadius = leftCorners;
        SettingsSection.CornerRadius = leftCorners;
    }

    private void RestoreWindowWidth()
    {
        var scale = Math.Clamp(SystemParameters.PrimaryScreenHeight / 1080.0, 1, 1.5);
        Width = _baseWindowWidth * scale;
        MaxWidth = 960;
    }
    private void StartLanguagePackWatchdog()
    {
        if (_languagePackTimer is not null) return;
        _languagePackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _languagePackTimer.Tick += LanguagePackWatchdogTick;
        _languagePackTimer.Start();
    }

    private void StopLanguagePackWatchdog()
    {
        if (_languagePackTimer is null) return;
        _languagePackTimer.Stop();
        _languagePackTimer = null;
    }

    private void LanguagePackWatchdogTick(object? sender, EventArgs e)
    {
        if (_pendingLanguageAppTag is null) return;

        var winTag = AppLangToWindowsTag(_pendingLanguageAppTag);
        if (winTag is null) return;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            return;

        var lang = new Language(winTag);
        var engine = OcrEngine.TryCreateFromLanguage(lang);
        if (engine is null)
            return;

        // Pack is now installed — hide banner, stop polling
        StopLanguagePackWatchdog();
        _pendingLanguageAppTag = null;
        OcrLanguageWarning.Visibility = Visibility.Collapsed;
        _sink.Emit("Windows OCR language pack installed — OCR engine will reinitialize.", "green");
    }
    private static string? AppLangToWindowsTag(string appLang)
    {
        return appLang?.ToLowerInvariant() switch
        {
            "eng" => "en-US",
            "fra" => "fr-FR",
            "deu" => "de-DE",
            "spa" => "es-ES",
            "por" => "pt-BR",
            "rus" => "ru-RU",
            "tha" => "th-TH",
            "chi_tra" => "zh-TW",
            "kor" => "ko-KR",
            "jpn" => "ja-JP",
            _ => null
        };
    }

    private void RefreshDebugMetrics()
    {
        if (_metrics is null) return;

        var snap = _metrics.GetSnapshot();

        DbgScansPerSec.Text = snap.ScansPerSecond > 0 ? $"{snap.ScansPerSecond:F1}" : "—";
        DbgUncachedAvg.Text = snap.AverageUncachedDurationMs > 0 ? $"{snap.AverageUncachedDurationMs:F1}ms" : "—";
        DbgCachedAvg.Text = snap.AverageCachedDurationMs > 0 ? $"{snap.AverageCachedDurationMs:F1}ms" : "—";
        DbgMinDuration.Text = snap.AverageOverheadMs > 0 ? $"{snap.AverageOverheadMs:F1}ms" : "—";

        DbgCacheHits.Text = snap.CacheHits > 0 ? $"{snap.CacheHits:N0}" : "—";
        DbgCachedScans.Text = snap.CacheHits > 0 ? $"{snap.CacheHits:N0}" : "—";
        DbgUncachedScans.Text = snap.FullOcrScans > 0 ? $"{snap.FullOcrScans:N0}" : "—";

        var rate = snap.CacheHitRate;
        DbgCacheRate.Text = rate > 0 ? $"{rate:F1}%" : "—";
        DbgCacheRate.Foreground = rate switch
        {
            >= 60 => (Brush)FindResource("GreenBrush"),
            >= 30 => (Brush)FindResource("AmberBrush"),
            > 0 => (Brush)FindResource("RedBrush"),
            _ => (Brush)FindResource("TextPrimary")
        };

        var slots = snap.SlotAveragesMs;
        SetSlotText(DbgSlotTotal, slots, DebugMetricsCollector.SlotIndex.Total);
        SetSlotText(DbgSlotCapture, slots, DebugMetricsCollector.SlotIndex.Capture);
        SetSlotText(DbgSlotAnchor, slots, DebugMetricsCollector.SlotIndex.AnchorCheck);
        SetSlotText(DbgSlotRecognize, slots, DebugMetricsCollector.SlotIndex.Recognize);
        DbgSlotCacheHit.Text = snap.CacheHits > 0 ? $"{snap.CacheHits:N0}" : "—";
        // Populate full layout slots (Tesseract)
        SetSlotText(DbgSlotTotalFull, slots, DebugMetricsCollector.SlotIndex.Total);
        SetSlotText(DbgSlotCaptureFull, slots, DebugMetricsCollector.SlotIndex.Capture);
        SetSlotText(DbgSlotAnchorFull, slots, DebugMetricsCollector.SlotIndex.AnchorCheck);
        SetSlotText(DbgSlotFrameHash, slots, DebugMetricsCollector.SlotIndex.FrameHash);
        SetSlotText(DbgSlotKeepBlack, slots, DebugMetricsCollector.SlotIndex.KeepBlack);
        SetSlotText(DbgSlotPreproc, slots, DebugMetricsCollector.SlotIndex.Preprocess);
        SetSlotText(DbgSlotUpscale, slots, DebugMetricsCollector.SlotIndex.Upscale);
        SetSlotText(DbgSlotPixEnc, slots, DebugMetricsCollector.SlotIndex.PixEncode);
        SetSlotText(DbgSlotRecognizeFull, slots, DebugMetricsCollector.SlotIndex.Recognize);
        SetSlotText(DbgSlotTsv, slots, DebugMetricsCollector.SlotIndex.TsvParse);
        SetSlotText(DbgSlotPost, slots, DebugMetricsCollector.SlotIndex.PostProcess);
        _ = (DbgSlotCacheHitFull?.Text = snap.CacheHits > 0 ? $"{snap.CacheHits:N0}" : "—");

        // Toggle slot breakdown layout based on OCR backend.
        // Fall back to the combo box if the metrics snapshot hasn't updated yet.
        var backendFromSnapshot = snap.OcrBackend;
        var backendFromCombo = (OcrBackendCombo.SelectedItem as string)?.ToLowerInvariant() ?? "";
        var isTesseract = backendFromSnapshot.Contains("tesseract", StringComparison.OrdinalIgnoreCase)
            || backendFromCombo.Contains("tesseract", StringComparison.OrdinalIgnoreCase);
        _ = (SlotBreakdownCompact?.Visibility = isTesseract ? Visibility.Collapsed : Visibility.Visible);
        _ = (SlotBreakdownFull?.Visibility = isTesseract ? Visibility.Visible : Visibility.Collapsed);

        DbgWindowStatus.Text = snap.IsPoe2Foreground ? "Foreground" : "Not active";
        DbgWindowStatus.Foreground = snap.IsPoe2Foreground
            ? (Brush)FindResource("GreenBrush")
            : (Brush)FindResource("AmberBrush");

        DbgInterfaceStatus.Text = snap.InterfaceDetected ? "✓ Detected" : "✗ Not visible";
        DbgInterfaceStatus.Foreground = snap.InterfaceDetected
            ? (Brush)FindResource("GreenBrush")
            : (Brush)FindResource("RedBrush");

        DbgCaptureMethod.Text = string.IsNullOrEmpty(snap.CaptureMethod) ? "—" : snap.CaptureMethod;
        var lsRunning = Process.GetProcessesByName("LosslessScaling").Length > 0;
        DbgLsStatus.Text = lsRunning ? "\u25CF Running" : "Not running";
        DbgLsStatus.Foreground = lsRunning
            ? (Brush)FindResource("AmberBrush")
            : (Brush)FindResource("TextSecondary");

        DbgOcrBackend.Text = string.IsNullOrEmpty(snap.OcrBackend) ? "—" : snap.OcrBackend;
        DbgRegion.Text = string.IsNullOrEmpty(snap.RegionInfo) ? "—" : snap.RegionInfo;

        DbgOverlayFps.Text = snap.DebugOverlayActive ? $"{snap.ScansPerSecond:F0}" : "—";

        DbgUptime.Text = snap.Uptime.TotalHours >= 1
            ? $"{(int)snap.Uptime.TotalHours}h {snap.Uptime.Minutes}m {snap.Uptime.Seconds}s"
            : snap.Uptime.TotalMinutes >= 1
                ? $"{snap.Uptime.Minutes}m {snap.Uptime.Seconds}s"
                : $"{snap.Uptime.Seconds}s";
        DbgCpuPercent.Text = snap.CpuPercent > 0 ? $"{snap.CpuPercent:F1}%" : "—";
        DbgCpuPercent.Foreground = snap.CpuPercent switch
        {
            > 30 => (Brush)FindResource("RedBrush"),
            > 15 => (Brush)FindResource("AmberBrush"),
            > 0 => (Brush)FindResource("GreenBrush"),
            _ => (Brush)FindResource("TextPrimary")
        };
        DbgMemory.Text = snap.MemoryMb > 0 ? $"{snap.MemoryMb}MB" : "—";
        DbgScanCpu.Text = snap.ScanCpuPercent > 0 ? $"{snap.ScanCpuPercent:F1}%" : "—";
        DbgScanCpu.Foreground = snap.ScanCpuPercent switch
        {
            > 20 => (Brush)FindResource("RedBrush"),
            > 10 => (Brush)FindResource("AmberBrush"),
            > 0 => (Brush)FindResource("GreenBrush"),
            _ => (Brush)FindResource("TextPrimary")
        };

        var recognizeMs = snap.SlotAveragesMs is { Length: > 8 } ? snap.SlotAveragesMs[8] : 0d;
        DbgRecognizeCpu.Text = recognizeMs > 0
            ? $"{recognizeMs:F0}ms/scan"
            : "—";
    }

    private static void SetSlotText(TextBlock block, double[] slots, int slotIndex)
    {
        if (slots is null || slotIndex < 0 || slotIndex >= slots.Length)
        {
            block.Text = "—";
            return;
        }
        var ms = slots[slotIndex];
        block.Text = ms > 0 ? $"{ms:F1}ms" : "—";
    }

    private void CopyDebug_Click(object sender, RoutedEventArgs e)
    {
        if (_metrics is null) return;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"=== RuneshapePriceChecker {VersionRun.Text} - Debug Metrics - copied at {now} ===");
        _ = sb.AppendLine();

        var snap = _metrics.GetSnapshot();

        _ = sb.AppendLine("── OCR Engine ──");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Scans/s:        {snap.ScansPerSecond:F1}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Uncached Avg:   {snap.AverageUncachedDurationMs:F1}ms");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Cached Avg:     {snap.AverageCachedDurationMs:F1}ms");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Overhead:       {snap.AverageOverheadMs:F1}ms");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Cache Hit Rate: {snap.CacheHitRate:F1}%");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Total Scans:    {snap.TotalScans:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Full OCR:       {snap.FullOcrScans:N0}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Cache Hits:     {snap.CacheHits:N0}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("── Slot Breakdown (avg ms) ──");
        var slots = snap.SlotAveragesMs;
        if (slots is { Length: > 0 })
        {
            for (var i = 0; i < slots.Length; i++)
            {
                var name = i switch
                {
                    DebugMetricsCollector.SlotIndex.Total => "Total",
                    DebugMetricsCollector.SlotIndex.Capture => "Capture",
                    DebugMetricsCollector.SlotIndex.AnchorCheck => "AnchorCheck",
                    DebugMetricsCollector.SlotIndex.FrameHash => "FrameHash",
                    DebugMetricsCollector.SlotIndex.KeepBlack => "KeepBlack",
                    DebugMetricsCollector.SlotIndex.Preprocess => "Preprocess",
                    DebugMetricsCollector.SlotIndex.Upscale => "Upscale",
                    DebugMetricsCollector.SlotIndex.PixEncode => "PixEncode",
                    DebugMetricsCollector.SlotIndex.Recognize => "Recognize",
                    DebugMetricsCollector.SlotIndex.TsvParse => "TsvParse",
                    DebugMetricsCollector.SlotIndex.PostProcess => "PostProcess",
                    DebugMetricsCollector.SlotIndex.CacheHit => "CacheHit",
                    _ => $"Slot{i}"
                };
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {name}: {slots[i]:F1}ms");
            }
        }
        _ = sb.AppendLine();

        _ = sb.AppendLine("── Status ──");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Window:    {DbgWindowStatus.Text}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Interface: {DbgInterfaceStatus.Text}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Capture:   {snap.CaptureMethod}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  LS:        {DbgLsStatus.Text}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  OCR:       {snap.OcrBackend}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Region:    {snap.RegionInfo}");
        _ = sb.AppendLine();

        var recognizeMs = snap.SlotAveragesMs is { Length: > 8 } ? snap.SlotAveragesMs[8] : 0d;
        _ = sb.AppendLine("── System ──");
        var uptime = snap.Uptime;
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Uptime:       {(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Memory:       {snap.MemoryMb}MB");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  CPU (proc):   {snap.CpuPercent:F1}%");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  CPU (scan):   {snap.ScanCpuPercent:F1}%");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  Recognize:    {recognizeMs:F0}ms/scan");

        Clipboard.SetText(sb.ToString());
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var header = $"=== RuneshapePriceChecker {VersionRun.Text} — copied at {now} ==={Environment.NewLine}{Environment.NewLine}";
        var lines = new string[LogEntries.Count];
        for (var i = 0; i < LogEntries.Count; i++)
        {
            var entry = LogEntries[LogEntries.Count - 1 - i];
            var count = string.IsNullOrEmpty(entry.CountText) ? "" : $" {entry.CountText}";
            lines[i] = $"{entry.Timestamp:HH:mm:ss.fff}  {entry.MessageText}{count}";
        }
        var body = string.Join(Environment.NewLine, lines);
        Clipboard.SetText(header + body);
    }

    private static void RestartApp()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c \"timeout /t 1 /nobreak >nul && start \"\" \"{exePath}\" --App:SuppressAlreadyRunningWarning=true\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { }

        _ = Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }

    private async Task LoadLeaguesAsync()
    {
        try
        {
            var leagues = await LeagueListService.FetchLeaguesAsync();

            await Dispatcher.InvokeAsync(() =>
            {
                LeagueCombo.Items.Clear();
                foreach (var league in leagues)
                    _ = LeagueCombo.Items.Add(league);

                for (var i = 0; i < LeagueCombo.Items.Count; i++)
                {
                    if (string.Equals(LeagueCombo.Items[i] as string, _vm.CurrentLeague, StringComparison.OrdinalIgnoreCase))
                    {
                        LeagueCombo.SelectedIndex = i;
                        return;
                    }
                }

                _ = LeagueCombo.Items.Add(_vm.CurrentLeague);
                LeagueCombo.SelectedIndex = LeagueCombo.Items.Count - 1;
            });
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _ = LeagueCombo.Items.Add(_vm.CurrentLeague);
                LeagueCombo.SelectedIndex = 0;
            });
        }
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        ClearValidation();
        ValidationError.Text = "";
        SyncViewModelFromUi();
        var error = _vm.SaveSettings();
        if (error is not null) { ShowValidation(error, RedThresholdBox); return; }
        ToggleSettings();
        UnlockStatus();

        if (_vm.LogLevelChanged)
        {
            RestartApp();
            return;
        }
    }

    private void ThresholdBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        var proposed = box.Text.Insert(box.SelectionStart, e.Text);
        if (proposed.Length > box.MaxLength) { e.Handled = true; return; }
        e.Handled = !decimal.TryParse(proposed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
    }

    private void ThresholdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        ValidateThresholds();
    }

    private void ValidateThresholds()
    {
        ClearValidation();
        var valid = true;
        string? error = null;

        var redOk = decimal.TryParse(RedThresholdBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var red)
                    && red >= 0.1m && red <= 999m;
        var orangeOk = decimal.TryParse(OrangeThresholdBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var orange)
                       && orange >= 0.1m && orange <= 999m;
        var greenOk = decimal.TryParse(GreenThresholdBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var green)
                      && green >= 0.1m && green <= 999m;

        if (!redOk) { valid = false; MarkInvalid(RedThresholdBox); }
        if (!orangeOk) { valid = false; MarkInvalid(OrangeThresholdBox); }
        if (!greenOk) { valid = false; MarkInvalid(GreenThresholdBox); }
        if (redOk && orangeOk && !(red < orange))
        {
            valid = false;
            error = "Red should be less than orange";
            MarkInvalid(RedThresholdBox);
        }
        else if (orangeOk && greenOk && !(orange < green))
        {
            valid = false;
            error = "Orange should be less than green";
            MarkInvalid(OrangeThresholdBox);
        }

        if (error is not null)
        {
            LogError(error);
            SetStatus(error, "red");
        }
        else if (!valid)
        {
            LogError("Invalid threshold values");
            SetStatus("Invalid threshold values", "red");
        }
        else
            ClearValidationStatus();
    }

    private static void MarkInvalid(Control target)
    {
        target.Tag = "invalid";
        target.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        target.BorderThickness = new Thickness(1);
    }

    private void ClearValidationStatus()
    {
        if (StatusLabel.Foreground is SolidColorBrush b && b.Color.R == 0xF8)
        {
            StatusLabel.Text = "\u25cf Ready";
            StatusLabel.Foreground = (Brush)FindResource("GreenBrush");
            _statusLockedUntil = DateTime.MinValue;
        }
    }

    private void UnlockStatus()
    {
        _statusLockedUntil = DateTime.MinValue;
    }

    private void Window_LocationChanged(object sender, EventArgs e)
    {
        ScheduleMoveResizeSave();
    }

    private void TooltipIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var tip = element.ToolTip as string;
            if (string.IsNullOrEmpty(tip))
                return;

            TooltipPopupText.Text = tip;

            // Measure while Hidden (participates in layout, not rendered) to get real size
            TooltipBorder.Visibility = Visibility.Hidden;
            TooltipBorder.Measure(new Size(320, double.PositiveInfinity));
            TooltipBorder.Arrange(new Rect(TooltipBorder.DesiredSize));
            var tipW = TooltipBorder.DesiredSize.Width;
            var tipH = TooltipBorder.DesiredSize.Height;

            var mouseX = e.GetPosition(this).X;
            var elemBounds = element.TransformToAncestor(this).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var m = 4d;

            // Center horizontally on the mouse cursor
            var x = mouseX - tipW / 2;
            // Show below the element by default
            var y = elemBounds.Bottom + 4;

            // If would overflow right edge, flip to left of element
            if (x + tipW > ActualWidth - m)
                x = elemBounds.Left - tipW - m;
            // If would overflow left edge, flip to right of element
            if (x < m)
                x = elemBounds.Right + m;
            // Last resort: clamp to window edges (tooltip wider than window)
            if (x + tipW > ActualWidth - m)
                x = ActualWidth - tipW - m;
            if (x < m)
                x = m;

            // If would overflow bottom, flip above
            if (y + tipH > ActualHeight - m)
                y = elemBounds.Top - tipH - m;
            if (y < m)
                y = m;

            TooltipBorder.SetValue(Canvas.LeftProperty, x);
            TooltipBorder.SetValue(Canvas.TopProperty, y);
            TooltipBorder.Visibility = Visibility.Visible;
        }
    }

    private void TooltipIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        TooltipBorder.Visibility = Visibility.Collapsed;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleMoveResizeSave();
    }

    private void ScheduleMoveResizeSave()
    {
        _moveResizeTimer?.Stop();
        _moveResizeTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(600),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _moveResizeTimer?.Stop();
                SaveWindowPosition();
            },
            Dispatcher);
        _moveResizeTimer.Start();
    }

    private void ShowValidation(string message, Control target)
    {
        ValidationError.Text = message;
        target.Tag = "invalid";
        target.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        target.BorderThickness = new Thickness(1);
        _ = target.Focus();
    }

    private void ClearValidation()
    {
        ClearValidationFor(RedThresholdBox);
        ClearValidationFor(OrangeThresholdBox);
        ClearValidationFor(GreenThresholdBox);
    }

    private static void ClearValidationFor(Control target)
    {
        target.Tag = null;
        target.ClearValue(BorderBrushProperty);
        target.ClearValue(BorderThicknessProperty);
    }

    private void CurrencyChaos_Checked(object sender, RoutedEventArgs e) { if (!_loading) CurrencyExaltCheck.IsChecked = false; }
    private void CurrencyExalt_Checked(object sender, RoutedEventArgs e) { if (!_loading) CurrencyChaosCheck.IsChecked = false; }
    private void CurrencyChaos_Unchecked(object sender, RoutedEventArgs e) { if (!_loading && CurrencyExaltCheck.IsChecked != true) CurrencyChaosCheck.IsChecked = true; }
    private void CurrencyExalt_Unchecked(object sender, RoutedEventArgs e) { if (!_loading && CurrencyChaosCheck.IsChecked != true) CurrencyExaltCheck.IsChecked = true; }

    private void DebugOverlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = DebugOverlayCheck.IsChecked == true;
        HideDebugOverlayCheck.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SaveDebugImagesCheck.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreWindowPosition()
    {
        Width = _debugPanelOpen
            ? (_baseWindowWidth + DebugPanelWidth) * Math.Clamp(SystemParameters.PrimaryScreenHeight / 1080.0, 1, 1.5)
            : 500;
        var pos = _vm.RestoreWindowPosition();

        if (pos is { } p && !double.IsNaN(p.Left) && !double.IsNaN(p.Top)
            && p.Left >= SystemParameters.VirtualScreenLeft
            && p.Top >= SystemParameters.VirtualScreenTop
            && p.Left + Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && p.Top + (double.IsNaN(p.Height) ? Height : Math.Min(p.Height, SystemParameters.VirtualScreenHeight)) <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            Left = p.Left;
            Top = p.Top;
            if (!double.IsNaN(p.Height) && p.Height >= MinHeight)
                Height = Math.Min(p.Height, SystemParameters.VirtualScreenHeight);
        }
        else
        {
            Left = (SystemParameters.WorkArea.Width - Width) / 2 + SystemParameters.WorkArea.Left;
            Top = (SystemParameters.WorkArea.Height - Height) / 2 + SystemParameters.WorkArea.Top;
        }
    }

    private void SaveWindowPosition()
    {
        if (WindowState != WindowState.Normal) return;
        _vm.SaveWindowPosition(Left, Top, 500, Height);
    }
    public void SetGameLanguage(string code)
    {
        _vm.OcrLanguage = code;
        // Auto-switch to Tesseract if Windows OCR was selected but doesn't support this language
        if (string.Equals(code, "rus", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_vm.OcrBackend, "windows", StringComparison.OrdinalIgnoreCase))
        {
            _vm.OcrBackend = "tesseract";
        }
        PopulateOcrBackendCombo(code);
    }
    public string GameLanguage => _vm.OcrLanguage;

    private static readonly bool _windowsOcrSupported = Environment.OSVersion.Version.Build >= 17763;

    private void PopulateOcrBackendCombo(string? language = null)
    {
        var lang = language ?? _vm.OcrLanguage;
        OcrBackendCombo.Items.Clear();
        if (_windowsOcrSupported && !string.Equals(lang, "rus", StringComparison.OrdinalIgnoreCase))
        {
            _ = OcrBackendCombo.Items.Add("Windows");
            _ = OcrBackendCombo.Items.Add("Tesseract");
            OcrBackendCombo.ToolTip = "Windows OCR is faster and uses less CPU. Only switch to Tesseract if Windows OCR isn't working correctly for you.";
            OcrBackendCombo.IsEnabled = true;
        }
        else if (_windowsOcrSupported && string.Equals(lang, "rus", StringComparison.OrdinalIgnoreCase))
        {
            _ = OcrBackendCombo.Items.Add("Tesseract");
            OcrBackendCombo.ToolTip = "Windows OCR does not support Russian text recognition reliably. Tesseract is used automatically for Russian.";
            OcrBackendCombo.IsEnabled = false;
        }
        else
        {
            _ = OcrBackendCombo.Items.Add("Tesseract");
            OcrBackendCombo.ToolTip = "Windows OCR requires Windows 10 build 1809 or later. Only Tesseract is available on this system.";
            OcrBackendCombo.IsEnabled = false;
        }
        // Sync the info icon's tooltip text with the combo's tooltip
        _ = (OcrBackendTooltip?.ToolTip = OcrBackendCombo.ToolTip);
        OcrBackendCombo.SelectedIndex = 0;
        UpdateOcrBackendWarning();
    }

    private void UpdateOcrBackendWarning()
    {
        if (OcrBackendWarning is null) return;
        var isTesseract = string.Equals(OcrBackendCombo.SelectedItem as string, "Tesseract", StringComparison.OrdinalIgnoreCase);
        var isRussian = string.Equals(_vm.OcrLanguage, "rus", StringComparison.OrdinalIgnoreCase);
        // Show warning icon when Tesseract is used but Windows OCR is available —
        // except for Russian where Windows OCR doesn't work, then show the info icon.
        var showWarning = _windowsOcrSupported && isTesseract && !isRussian;
        OcrBackendWarning.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;
        _ = (OcrBackendTooltip?.Visibility = showWarning ? Visibility.Collapsed : Visibility.Visible);
    }

    private void OcrBackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateOcrBackendWarning();
    }

    private void PopulatePricingSourceCombo()
    {
        PricingSourceCombo.Items.Clear();
        _ = PricingSourceCombo.Items.Add("poe2scout");
        _ = PricingSourceCombo.Items.Add("poe.ninja");
        PricingSourceCombo.SelectedIndex = 0;
    }

    private void PopulateLogLevelCombo()
    {
        LogLevelCombo.Items.Clear();
        _ = LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Trace", Tag = "Trace" });
        _ = LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Debug", Tag = "Debug" });
        _ = LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Information", Tag = "Information" });
        LogLevelCombo.SelectedIndex = 2; // Information
    }
}

public sealed class LogEntryViewModel : INotifyPropertyChanged
{
    public string RawMessage { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public int Count { get; set; } = 1;
    public Brush? ForegroundBrush { get; set; }
    public Microsoft.Extensions.Logging.LogLevel LogLevel { get; set; }

    public string TimestampText { get; set { field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimestampText))); } } = "";

    public string MessageText { get; set { field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MessageText))); } } = "";

    public string CountText { get; set { field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText))); } } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateDisplayText()
    {
        TimestampText = $"{Timestamp:HH:mm:ss}";
        CountText = Count > 1 ? $"(x{Count})" : "";
    }

    public void SetInitialText()
    {
        MessageText = RawMessage;
        UpdateDisplayText();
    }
}
// force rebuild 17:04:26