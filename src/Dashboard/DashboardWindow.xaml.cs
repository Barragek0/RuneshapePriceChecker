using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed partial class DashboardWindow : Window
{
    private readonly DashboardLogSink _sink;
    private readonly string _configPath;
    private double _baseWindowWidth = 520;
    private double _baseWindowHeight = 685;
    private bool _loading;
    private string _currentLeague = "Runes of Aldur";

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

    public DashboardWindow(DashboardLogSink sink)
    {
        _sink = sink;
        _configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
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

        _sink.OnLogEntry += OnLogEntry;
        PopulateLanguageCombo();
        PopulatePricingSourceCombo();
        PopulateLogLevelCombo();
        LoadSettings();
        _ = LoadLeaguesAsync();

        if (HasCommandLineArg("--App:ForceUpdateAvailable=true") ||
            ConfigHasFlag("App", "ForceUpdateAvailable"))
            ShowUpdateButton();

        if (HasCommandLineArg("--App:AutoApplyUpdate=true") ||
            ConfigHasFlag("App", "AutoApplyUpdate"))
        {
            ShowUpdateButton();
            Loaded += async (_, _) =>
            {
                for (var i = 0; i < 30; i++)
                {
                    if (_onUpdateTriggered is not null) break;
                    await Task.Delay(500);
                }
                if (_onUpdateTriggered is not null)
                    Dispatcher.Invoke(() => Update_Click(this, new RoutedEventArgs()));
            };
        }
    }

    private bool ConfigHasFlag(string section, string key)
    {
        try
        {
            if (!File.Exists(_configPath)) return false;
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            return root?[section]?[key]?.GetValue<bool>() == true;
        }
        catch { return false; }
    }

    private static bool HasCommandLineArg(string arg)
    {
        return Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase));
    }

    private void InitializeScale()
    {
        var h = SystemParameters.PrimaryScreenHeight;
        var scale = Math.Clamp(h / 1080.0, 1, 1.5);

        Width = _baseWindowWidth * scale;
        Height = _baseWindowHeight * scale;
    }

    private void OnLogEntry(LogEntry entry)
    {
        Dispatcher.Invoke(() =>
        {
            var brush = entry.Color switch
            {
                "red" => (Brush)FindResource("RedBrush"),
                "yellow" => (Brush)FindResource("YellowBrush"),
                "white" => (Brush)FindResource("TextPrimary"),
                _ => (Brush)FindResource("GreenBrush")
            };

            for (int i = 0; i < LogEntries.Count; i++)
            {
                if (string.Equals(LogEntries[i].RawMessage, entry.Message, StringComparison.Ordinal))
                {
                    var existing = LogEntries[i];
                    existing.Count = entry.Count;
                    existing.Timestamp = entry.Timestamp;
                    existing.UpdateDisplayText();
                    if (i != 0)
                        LogEntries.Move(i, 0);
                    return;
                }
            }

            var vm = new LogEntryViewModel
            {
                RawMessage = entry.Message,
                Timestamp = entry.Timestamp,
                Count = entry.Count,
                ForegroundBrush = brush
            };
            vm.SetInitialText();

            LogEntries.Insert(0, vm);

            while (LogEntries.Count > 1000)
                LogEntries.RemoveAt(LogEntries.Count - 1);
        });
    }

    public void SetStatus(string text, string color = "green")
    {
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
    }

    private Action? _onSetupContinue;

    public void SetOnSetupContinue(Action callback)
    {
        _onSetupContinue = callback;
    }

    public void ShowSetupPrompt()
    {
        Dispatcher.Invoke(() =>
        {
            LogSection.Visibility = Visibility.Collapsed;
            SettingsSection.Visibility = Visibility.Collapsed;
            SetupPromptSection.Visibility = Visibility.Visible;
        });
    }

    public void HideSetupPrompt()
    {
        Dispatcher.Invoke(() =>
        {
            SetupPromptSection.Visibility = Visibility.Collapsed;
            LogSection.Visibility = Visibility.Visible;
        });
    }

    private void SetupContinue_Click(object sender, RoutedEventArgs e)
    {
        _onSetupContinue?.Invoke();
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
            return (IntPtr)1;
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
                return (nint)HTCLIENT;
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

            if (atTop && atLeft) { handled = true; return (nint)HTTOPLEFT; }
            if (atTop && atRight) { handled = true; return (nint)HTTOPRIGHT; }
            if (atBottom && atLeft) { handled = true; return (nint)HTBOTTOMLEFT; }
            if (atBottom && atRight) { handled = true; return (nint)HTBOTTOMRIGHT; }
            if (atTop) { handled = true; return (nint)HTTOP; }
            if (atBottom) { handled = true; return (nint)HTBOTTOM; }
            if (atLeft) { handled = true; return (nint)HTLEFT; }
            if (atRight) { handled = true; return (nint)HTRIGHT; }

            handled = true;
            return (nint)HTCLIENT;
        }

        return IntPtr.Zero;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ToggleSettings();
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_onUpdateTriggered is null) return;
        ShowUpdateOverlay();
        SetUpdateProgress(0);
        var progress = new Progress<int>(SetUpdateProgress);
        _onUpdateTriggered(progress);
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

    private Action<IProgress<int>>? _onUpdateTriggered;

    public void SetUpdateTrigger(Action<IProgress<int>> trigger)
    {
        _onUpdateTriggered = trigger;
    }

    public void ShowUpdateButton()
    {
        Dispatcher.Invoke(() => UpdateBadge.Visibility = Visibility.Visible);
    }

    public void HideUpdateButton()
    {
        Dispatcher.Invoke(() => UpdateBadge.Visibility = Visibility.Collapsed);
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

    private void ToggleSettings()
    {
        var showingSettings = SettingsSection.Visibility == Visibility.Visible;
        SettingsSection.Visibility = showingSettings ? Visibility.Collapsed : Visibility.Visible;
        LogSection.Visibility = showingSettings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ReRunSetup_Click(object sender, RoutedEventArgs e)
    {
        ToggleSettings();
        _onReRunSetup?.Invoke();
    }

    private Action? _onReRunSetup;

    public void SetReRunSetupTrigger(Action trigger)
    {
        _onReRunSetup = trigger;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BringToFront();
    }

    private void BringToFront()
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
        var text = string.Join(Environment.NewLine,
            LogEntries.Reverse().Select(e => $"{e.TimestampText}  {e.MessageText}"));
        Clipboard.SetText(text);
    }

    private async Task LoadLeaguesAsync()
    {
        try
        {
            var service = new LeagueListService();
            var leagues = await service.FetchLeaguesAsync();

            await Dispatcher.InvokeAsync(() =>
            {
                LeagueCombo.Items.Clear();
                foreach (var league in leagues)
                    LeagueCombo.Items.Add(league);

                for (var i = 0; i < LeagueCombo.Items.Count; i++)
                {
                    if (string.Equals(LeagueCombo.Items[i] as string, _currentLeague, StringComparison.OrdinalIgnoreCase))
                    {
                        LeagueCombo.SelectedIndex = i;
                        return;
                    }
                }

                LeagueCombo.Items.Add(_currentLeague);
                LeagueCombo.SelectedIndex = LeagueCombo.Items.Count - 1;
            });
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LeagueCombo.Items.Add(_currentLeague);
                LeagueCombo.SelectedIndex = 0;
            });
        }
    }

    private void LoadSettings()
    {
        _loading = true;
        if (!File.Exists(_configPath)) { _loading = false; return; }

        try
        {
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            if (root is null) { _loading = false; return; }

            if (root["App"] is JsonNode app)
            {
                var logLevelStr = app["LogLevel"]?.GetValue<string>() ?? "Information";
                for (var i = 0; i < LogLevelCombo.Items.Count; i++)
                {
                    if (LogLevelCombo.Items[i] is ComboBoxItem item &&
                        string.Equals(item.Tag as string, logLevelStr, StringComparison.OrdinalIgnoreCase))
                    {
                        LogLevelCombo.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (root["Pricing"] is JsonNode pricing)
            {
                _currentLeague = pricing["League"]?.GetValue<string>() ?? "Runes of Aldur";
                var source = pricing["PricingSource"]?.GetValue<string>() ?? "poe2scout";
                for (var i = 0; i < PricingSourceCombo.Items.Count; i++)
                {
                    if (string.Equals(PricingSourceCombo.Items[i] as string, source, StringComparison.OrdinalIgnoreCase))
                    {
                        PricingSourceCombo.SelectedIndex = i;
                        break;
                    }
                }
                var currency = pricing["DisplayCurrency"]?.GetValue<string>() ?? "chaos";
                var isExalt = string.Equals(currency, "exalt", StringComparison.OrdinalIgnoreCase);
                CurrencyChaosCheck.IsChecked = !isExalt;
                CurrencyExaltCheck.IsChecked = isExalt;
                var red = pricing["RedThreshold"]?.GetValue<decimal>();
                var orange = pricing["OrangeThreshold"]?.GetValue<decimal>();
                var green = pricing["GreenThreshold"]?.GetValue<decimal>();
                RedThresholdBox.Text = red?.ToString() ?? "0.5";
                OrangeThresholdBox.Text = orange?.ToString() ?? "1.0";
                GreenThresholdBox.Text = green?.ToString() ?? "5.0";
            }

            if (root["OCR"] is JsonNode ocr)
            {
                DebugOverlayCheck.IsChecked = ocr["DebugOverlay"]?.GetValue<bool>() ?? false;
                HideDebugOverlayCheck.IsChecked = ocr["HideDebugOverlayWhenInterfaceNotDetected"]?.GetValue<bool>() ?? false;
                SaveDebugImagesCheck.IsChecked = ocr["SaveDebugImages"]?.GetValue<bool>() ?? false;
                ShowPricingOverlayCheck.IsChecked = ocr["ShowPricingOverlay"]?.GetValue<bool>() ?? true;
                ShowBannerCheck.IsChecked = ocr["ShowBanner"]?.GetValue<bool>() ?? true;

                var currentLang = ocr["Language"]?.GetValue<string>() ?? "eng";
                for (var i = 0; i < LanguageCombo.Items.Count; i++)
                {
                    if (LanguageCombo.Items[i] is ComboBoxItem item &&
                        string.Equals(item.Tag as string, currentLang, StringComparison.OrdinalIgnoreCase))
                    {
                        LanguageCombo.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (root["Update"] is JsonNode update)
                AutoUpdateCheck.IsChecked = update["AutoUpdate"]?.GetValue<bool>() ?? true;
        }
        catch { }
        finally { _loading = false; }
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        ValidationError.Text = "";

        var league = LeagueCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(league))
        {
            ShowValidation("Select a league.", LeagueCombo);
            return;
        }
        if (!decimal.TryParse(RedThresholdBox.Text, out var red))
        {
            ShowValidation("Red threshold must be a number.", RedThresholdBox);
            return;
        }
        if (!decimal.TryParse(OrangeThresholdBox.Text, out var orange))
        {
            ShowValidation("Orange threshold must be a number.", OrangeThresholdBox);
            return;
        }
        if (!decimal.TryParse(GreenThresholdBox.Text, out var green))
        {
            ShowValidation("Green threshold must be a number.", GreenThresholdBox);
            return;
        }
        if (!(red < orange && orange < green))
        {
            ShowValidation("Thresholds must be: Red < Orange < Green.", RedThresholdBox);
            return;
        }

        try
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (configDir is null) return;
            Directory.CreateDirectory(configDir);

            var existingJson = File.Exists(_configPath) ? File.ReadAllText(_configPath, Encoding.UTF8) : "{}";
            var root = JsonNode.Parse(existingJson) ?? new JsonObject();
            if (root is not JsonObject rootObj) return;

            rootObj["App"] ??= new JsonObject();
            rootObj["Pricing"] ??= new JsonObject();
            rootObj["OCR"] ??= new JsonObject();
            rootObj["Update"] ??= new JsonObject();

            if (rootObj["App"] is JsonObject app)
                app["LogLevel"] = (LogLevelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Information";

            if (rootObj["Pricing"] is JsonObject pricing)
            {
                pricing["PricingSource"] = PricingSourceCombo.SelectedItem as string ?? "poe2scout";
                pricing["League"] = league;
                pricing["DisplayCurrency"] = CurrencyExaltCheck.IsChecked == true ? "exalt" : "chaos";
                pricing["RedThreshold"] = red;
                pricing["OrangeThreshold"] = orange;
                pricing["GreenThreshold"] = green;
            }

            if (rootObj["OCR"] is JsonObject ocr)
            {
                ocr["DebugOverlay"] = DebugOverlayCheck.IsChecked == true;
                ocr["HideDebugOverlayWhenInterfaceNotDetected"] = HideDebugOverlayCheck.IsChecked == true;
                ocr["SaveDebugImages"] = SaveDebugImagesCheck.IsChecked == true;
                ocr["ShowPricingOverlay"] = ShowPricingOverlayCheck.IsChecked == true;
                ocr["ShowBanner"] = ShowBannerCheck.IsChecked == true;

                var newLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                if (!string.IsNullOrWhiteSpace(newLang) &&
                    !string.Equals(newLang, ocr["Language"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                {
                    ocr["Language"] = newLang;
                }
            }

            if (rootObj["Update"] is JsonObject update)
                update["AutoUpdate"] = AutoUpdateCheck.IsChecked == true;

            var jsonResult = rootObj.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(_configPath, jsonResult + Environment.NewLine, Encoding.UTF8);
            ToggleSettings();
        }
        catch (Exception ex)
        {
            ValidationError.Text = $"Save failed: {ex.Message}";
        }
    }

    private void SettingsCancel_Click(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        ToggleSettings();
    }

    private void ShowValidation(string message, Control target)
    {
        ValidationError.Text = message;
        target.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        target.BorderThickness = new Thickness(1);
        target.Focus();
    }

    private void CurrencyChaos_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        CurrencyExaltCheck.IsChecked = false;
    }

    private void CurrencyChaos_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (CurrencyExaltCheck.IsChecked != true)
            CurrencyChaosCheck.IsChecked = true;
    }

    private void CurrencyExalt_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        CurrencyChaosCheck.IsChecked = false;
    }

    private void CurrencyExalt_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (CurrencyChaosCheck.IsChecked != true)
            CurrencyExaltCheck.IsChecked = true;
    }

    private void RestoreWindowPosition()
    {
        try
        {
            if (!File.Exists(_configPath)) return;

            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            if (root?["Window"] is not JsonNode window) return;

            var left = window["Left"]?.GetValue<double>() ?? double.NaN;
            var top = window["Top"]?.GetValue<double>() ?? double.NaN;
            var width = window["Width"]?.GetValue<double>() ?? double.NaN;
            var height = window["Height"]?.GetValue<double>() ?? double.NaN;

            if (!double.IsNaN(width) && width >= MinWidth)
                Width = Math.Min(width, SystemParameters.VirtualScreenWidth);
            if (!double.IsNaN(height) && height >= MinHeight)
                Height = Math.Min(height, SystemParameters.VirtualScreenHeight);

            if (double.IsNaN(left) || double.IsNaN(top)) return;

            var virtualRect = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            if (left >= virtualRect.Left && top >= virtualRect.Top &&
                left + Width <= virtualRect.Right &&
                top + Height <= virtualRect.Bottom)
            {
                Left = left;
                Top = top;
            }
        }
        catch { }
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

    private void SaveWindowPosition()
    {
        try
        {
            if (WindowState != WindowState.Normal) return;

            var configDir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);

            JsonNode root;
            if (File.Exists(_configPath))
            {
                var existingJson = File.ReadAllText(_configPath, Encoding.UTF8);
                root = JsonNode.Parse(existingJson) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var windowNode = root["Window"] as JsonObject;
            if (windowNode is null)
            {
                windowNode = new JsonObject();
                root["Window"] = windowNode;
            }

            windowNode["Left"] = (int)Left;
            windowNode["Top"] = (int)Top;
            windowNode["Width"] = (int)Width;
            windowNode["Height"] = (int)Height;

            var json = root.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(_configPath, json + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}

public sealed class LogEntryViewModel : INotifyPropertyChanged
{
    public string RawMessage { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public int Count { get; set; } = 1;
    public Brush? ForegroundBrush { get; set; }

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
