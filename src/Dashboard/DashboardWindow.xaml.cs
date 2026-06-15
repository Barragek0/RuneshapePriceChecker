using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed partial class DashboardWindow : Window
{
    private readonly DashboardLogSink _sink;
    private readonly DashboardViewModel _vm;
    private readonly double _baseWindowWidth = 520;
    private readonly double _baseWindowHeight = 691;
    private bool _loading;
    private bool _changelogVisible;
    private bool _setupPending;
    private bool _settingsVisible;
    private System.Windows.Threading.DispatcherTimer? _moveResizeTimer;
    private DateTime _statusLockedUntil = DateTime.MinValue;

    public ObservableCollection<LogEntryViewModel> LogEntries => _vm.LogEntries;

    public event Action? ChangelogShown;
    public event Action? ChangelogDismissed;

    internal bool IsChangelogVisible => _changelogVisible;

    public DashboardWindow(DashboardLogSink sink)
    {
        _sink = sink;
        var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
        _vm = new DashboardViewModel(configPath);
        DataContext = this;
        InitializeComponent();
        InitializeScale();
        LogList.DataContext = this;

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.2.2";
        var plusIdx = version.IndexOf('+');
        if (plusIdx >= 0) version = version[..plusIdx];
        VersionRun.Text = $"v{version}";

        _sink.OnLogEntry += entry => Dispatcher.Invoke(() => _vm.OnLogEntry(entry));

        foreach (var entry in _sink.Snapshot().Reverse())
            _vm.OnLogEntry(entry);

        PopulateLanguageCombo();
        PopulatePricingSourceCombo();
        PopulateLogLevelCombo();
        _vm.LoadSettings();
        SyncUiFromViewModel();
        _ = LoadLeaguesAsync();

        CheckPendingChangelog();

        if (HasCommandLineArg("--ShowChangelog"))
        {
            Loaded += (_, _) => ShowChangelogPreview();
        }

        if (HasCommandLineArg("--App:ForceUpdateAvailable=true") || _vm.ConfigHasFlag("App", "ForceUpdateAvailable"))
            ShowUpdateButton();

        if (HasCommandLineArg("--App:AutoApplyUpdate=true") || _vm.ConfigHasFlag("App", "AutoApplyUpdate"))
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

        if (HasCommandLineArg("--App:TestMode=true"))
            TestModeIndicator.Visibility = Visibility.Visible;
    }

    private static bool HasCommandLineArg(string arg)
    {
        return Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase));
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
                Dispatcher.BeginInvoke(new Action(async () =>
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
        if (!File.Exists(changelogPath))
        {
            changelogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tests", "changelog-v0.2.0.md"));
        }
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
            _changelogVisible = true;
            var title = $"## v{version} Changelog\n\n";
            ChangelogViewer.Document = MarkdownRenderer.Render(title + body);
            RefreshContentArea();
        });
        ChangelogShown?.Invoke();
    }

    private void ChangelogClose_Click(object sender, RoutedEventArgs e)
    {
        _changelogVisible = false;
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
            if (string.Equals(PricingSourceCombo.Items[i] as string, _vm.PricingSource, StringComparison.OrdinalIgnoreCase))
            { PricingSourceCombo.SelectedIndex = i; break; }
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
        ShowPricingOverlayCheck.IsChecked = _vm.ShowPricingOverlay;
        ShowBannerCheck.IsChecked = _vm.ShowBanner;
        AutoUpdateCheck.IsChecked = _vm.AutoUpdate;
        for (var i = 0; i < LanguageCombo.Items.Count; i++)
        {
            if (LanguageCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, _vm.OcrLanguage, StringComparison.OrdinalIgnoreCase))
            { LanguageCombo.SelectedIndex = i; break; }
        }
        _loading = false;
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
        _vm.ShowPricingOverlay = ShowPricingOverlayCheck.IsChecked == true;
        _vm.ShowBanner = ShowBannerCheck.IsChecked == true;
        _vm.OcrLanguage = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "eng";
        _vm.AutoUpdate = AutoUpdateCheck.IsChecked == true;
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
        !_changelogVisible &&
        !_settingsVisible;

    public void SetOnSetupContinue(Action callback) => _vm.OnSetupContinue = callback;

    public void ShowSetupPrompt()
    {
        Dispatcher.Invoke(() => { _setupPending = true; RefreshContentArea(); });
    }

    public void HideSetupPrompt()
    {
        Dispatcher.Invoke(() => { _setupPending = false; RefreshContentArea(); });
    }

    private void RefreshContentArea()
    {
        ChangelogSection.Visibility = Visibility.Collapsed;
        SetupPromptSection.Visibility = Visibility.Collapsed;
        SettingsSection.Visibility = Visibility.Collapsed;
        LogSection.Visibility = Visibility.Collapsed;

        if (_changelogVisible)
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
    }

    private void SetupContinue_Click(object sender, RoutedEventArgs e) => _vm.OnSetupContinue?.Invoke();

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
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
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
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            var atTop = point.Y <= border;
            var atBottom = point.Y >= Height - border;
            var atLeft = point.X <= border;
            var atRight = point.X >= Width - border;

            if (atTop && atLeft) { handled = true; return HTTOPLEFT; }
            if (atTop && atRight) { handled = true; return HTTOPRIGHT; }
            if (atBottom && atLeft) { handled = true; return HTBOTTOMLEFT; }
            if (atBottom && atRight) { handled = true; return HTBOTTOMRIGHT; }
            if (atTop) { handled = true; return HTTOP; }
            if (atBottom) { handled = true; return HTBOTTOM; }
            if (atLeft) { handled = true; return HTLEFT; }
            if (atRight) { handled = true; return HTRIGHT; }

            handled = true;
            return HTCLIENT;
        }

        return IntPtr.Zero;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
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
            var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
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

            if (body is not null)
            {
                ShowChangelog(version, body);
            }
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

    public void SetUpdateTrigger(Action<IProgress<int>> trigger) => _vm.OnUpdateTriggered = trigger;
    public void ShowUpdateButton() => Dispatcher.Invoke(() =>
    {
        if (UpdateProgressPanel.Visibility == Visibility.Visible) return;
        UpdateBadge.Visibility = Visibility.Visible;
    });
    public void HideUpdateButton() => Dispatcher.Invoke(() => UpdateBadge.Visibility = Visibility.Collapsed);
    public void SetReRunSetupTrigger(Action trigger) => _vm.OnReRunSetup = trigger;

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
        var animation = new System.Windows.Media.Animation.DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1.2)))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
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
        var animation = new System.Windows.Media.Animation.DoubleAnimation(0, 360,
            new Duration(TimeSpan.FromSeconds(1)))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
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
        if (!_settingsVisible) _changelogVisible = false;
        _settingsVisible = !_settingsVisible;
        RefreshContentArea();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ReRunSetup_Click(object sender, RoutedEventArgs e) { ToggleSettings(); _vm.OnReRunSetup?.Invoke(); }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BringToFront();
    }

    internal void BringToFront()
    {
        Topmost = true;
        Activate();
        Dispatcher.BeginInvoke(new Action(() => Topmost = false),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        SaveWindowPosition();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var header = $"=== RuneshapePriceChecker {VersionRun.Text} — copied at {now} ==={Environment.NewLine}{Environment.NewLine}";
        var body = string.Join(Environment.NewLine,
            LogEntries.Reverse().Select(entry =>
            {
                var count = string.IsNullOrEmpty(entry.CountText) ? "" : $" {entry.CountText}";
                return $"{entry.TimestampText}  {entry.MessageText}{count}";
            }));
        Clipboard.SetText(header + body);
    }

    private static void RestartApp()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c \"timeout /t 1 /nobreak >nul && start \"\" \"{exePath}\" --App:SuppressAlreadyRunningWarning=true\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { }

        Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
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
                    LeagueCombo.Items.Add(league);

                for (var i = 0; i < LeagueCombo.Items.Count; i++)
                {
                    if (string.Equals(LeagueCombo.Items[i] as string, _vm.CurrentLeague, StringComparison.OrdinalIgnoreCase))
                    {
                        LeagueCombo.SelectedIndex = i;
                        return;
                    }
                }

                LeagueCombo.Items.Add(_vm.CurrentLeague);
                LeagueCombo.SelectedIndex = LeagueCombo.Items.Count - 1;
            });
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LeagueCombo.Items.Add(_vm.CurrentLeague);
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

        if (!redOk && RedThresholdBox.Text.Length > 0) { valid = false; MarkInvalid(RedThresholdBox); }
        if (!orangeOk && OrangeThresholdBox.Text.Length > 0) { valid = false; MarkInvalid(OrangeThresholdBox); }
        if (!greenOk && GreenThresholdBox.Text.Length > 0) { valid = false; MarkInvalid(GreenThresholdBox); }

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

        SaveBtn.IsEnabled = valid;
        SaveBtn.Opacity = valid ? 1.0 : 0.4;
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

    private void Window_LocationChanged(object sender, EventArgs e)
    {
        ScheduleMoveResizeSave();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleMoveResizeSave();
    }

    private void ScheduleMoveResizeSave()
    {
        _moveResizeTimer?.Stop();
        _moveResizeTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(600),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _moveResizeTimer?.Stop();
                SaveWindowPosition();
            },
            Dispatcher);
        _moveResizeTimer.Start();
    }

    private void SettingsCancel_Click(object sender, RoutedEventArgs e)
    {
        ClearValidationStatus();
        ClearValidation();
        _vm.LoadSettings();
        SyncUiFromViewModel();
        ToggleSettings();
    }

    private void ShowValidation(string message, Control target)
    {
        ValidationError.Text = message;
        target.Tag = "invalid";
        target.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        target.BorderThickness = new Thickness(1);
        target.Focus();
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
        target.ClearValue(Control.BorderBrushProperty);
        target.ClearValue(Control.BorderThicknessProperty);
    }

    private void CurrencyChaos_Checked(object sender, RoutedEventArgs e) { if (!_loading) CurrencyExaltCheck.IsChecked = false; }
    private void CurrencyExalt_Checked(object sender, RoutedEventArgs e) { if (!_loading) CurrencyChaosCheck.IsChecked = false; }
    private void CurrencyChaos_Unchecked(object sender, RoutedEventArgs e) { if (!_loading && CurrencyExaltCheck.IsChecked != true) CurrencyChaosCheck.IsChecked = true; }
    private void CurrencyExalt_Unchecked(object sender, RoutedEventArgs e) { if (!_loading && CurrencyChaosCheck.IsChecked != true) CurrencyExaltCheck.IsChecked = true; }

    private void RestoreWindowPosition()
    {
        var pos = _vm.RestoreWindowPosition();
        if (pos is not { } p) return;

        if (!double.IsNaN(p.Width) && p.Width >= MinWidth)
            Width = Math.Min(p.Width, SystemParameters.VirtualScreenWidth);
        if (!double.IsNaN(p.Height) && p.Height >= MinHeight)
            Height = Math.Min(p.Height, SystemParameters.VirtualScreenHeight);
        if (double.IsNaN(p.Left) || double.IsNaN(p.Top)) return;

        var vr = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (p.Left >= vr.Left && p.Top >= vr.Top && p.Left + Width <= vr.Right && p.Top + Height <= vr.Bottom)
        { Left = p.Left; Top = p.Top; }
    }

    private void SaveWindowPosition()
    {
        if (WindowState != WindowState.Normal) return;
        _vm.SaveWindowPosition(Left, Top, Width, Height);
    }

    private static readonly Dictionary<string, string> TesseractLanguageNames = new()
    {
        ["eng"] = "English",
        ["fra"] = "Français",
        ["deu"] = "Deutsch",
        ["por"] = "Português",
        ["rus"] = "Русский",
        ["tha"] = "ไทย",
        ["chi_tra"] = "繁體中文",
        ["spa"] = "Español",
        ["kor"] = "한국어",
        ["jpn"] = "日本語",
    };

    private void PopulateLanguageCombo()
    {
        LanguageCombo.Items.Clear();
        foreach (var (code, name) in TesseractLanguageNames)
        {
            LanguageCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        LanguageCombo.SelectedIndex = 0;
        for (var i = 0; i < LanguageCombo.Items.Count; i++)
        {
            if (LanguageCombo.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, "eng", StringComparison.OrdinalIgnoreCase))
            {
                LanguageCombo.SelectedIndex = i;
                break;
            }
        }
    }

    private void PopulatePricingSourceCombo()
    {
        PricingSourceCombo.Items.Clear();
        PricingSourceCombo.Items.Add("poe2scout");
        PricingSourceCombo.Items.Add("poe.ninja");
        PricingSourceCombo.SelectedIndex = 0;
    }

    private void PopulateLogLevelCombo()
    {
        LogLevelCombo.Items.Clear();
        LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Trace", Tag = "Trace" });
        LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Debug", Tag = "Debug" });
        LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Information", Tag = "Information" });
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

    private string _timestampText = "";
    public string TimestampText
    {
        get => _timestampText;
        set { _timestampText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimestampText))); }
    }

    private string _messageText = "";
    public string MessageText
    {
        get => _messageText;
        set { _messageText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MessageText))); }
    }

    private string _countText = "";
    public string CountText
    {
        get => _countText;
        set { _countText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText))); }
    }

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
