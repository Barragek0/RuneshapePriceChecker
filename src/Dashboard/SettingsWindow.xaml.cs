using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed partial class SettingsWindow : Window
{
    private readonly string _configPath;
    private bool _loading;
    private string _currentLeague = "Runes of Aldur";

    public SettingsWindow()
    {
        InitializeComponent();
        _configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
        PopulateLogLevelCombo();
        Loaded += async (_, _) => await LoadLeaguesAsync();
        LoadSettings();
    }

    private async Task LoadLeaguesAsync()
    {
        try
        {
            var leagues = await LeagueListService.FetchLeaguesAsync();

            LeagueCombo.Items.Clear();
            foreach (var league in leagues)
                _ = LeagueCombo.Items.Add(league);

            for (var i = 0; i < LeagueCombo.Items.Count; i++)
            {
                if (string.Equals(LeagueCombo.Items[i] as string, _currentLeague, StringComparison.OrdinalIgnoreCase))
                {
                    LeagueCombo.SelectedIndex = i;
                    return;
                }
            }

            _ = LeagueCombo.Items.Add(_currentLeague);
            LeagueCombo.SelectedIndex = LeagueCombo.Items.Count - 1;
        }
        catch
        {
            _ = LeagueCombo.Items.Add(_currentLeague);
            LeagueCombo.SelectedIndex = 0;
        }
    }

    private void LoadSettings()
    {
        _loading = true;

        if (!File.Exists(_configPath))
        {
            _loading = false;
            return;
        }

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
                var currency = pricing["DisplayCurrency"]?.GetValue<string>() ?? "chaos";
                var isExalt = string.Equals(currency, "exalt", StringComparison.OrdinalIgnoreCase);
                CurrencyChaosCheck.IsChecked = !isExalt;
                CurrencyExaltCheck.IsChecked = isExalt;
                AutoThresholdsCheck.IsChecked = pricing["AutoPriceThresholds"]?.GetValue<bool>() ?? true;
                RedThresholdBox.Text = pricing["RedThreshold"]?.GetValue<decimal>().ToString(CultureInfo.InvariantCulture) ?? "0.5";
                OrangeThresholdBox.Text = pricing["OrangeThreshold"]?.GetValue<decimal>().ToString(CultureInfo.InvariantCulture) ?? "1.0";
                GreenThresholdBox.Text = pricing["GreenThreshold"]?.GetValue<decimal>().ToString(CultureInfo.InvariantCulture) ?? "5.0";
            }
            UpdateThresholdVisibility();

            if (root["OCR"] is JsonNode ocr)
            {
                DebugOverlayCheck.IsChecked = ocr["DebugOverlay"]?.GetValue<bool>() ?? false;
                HideDebugOverlayCheck.IsChecked = ocr["HideDebugOverlayWhenInterfaceNotDetected"]?.GetValue<bool>() ?? false;
            }

            if (root["Update"] is JsonNode update)
                AutoUpdateCheck.IsChecked = update["AutoUpdate"]?.GetValue<bool>() ?? false;
        }
        catch { }
        finally { _loading = false; }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Save();
        Close();
    }

    private void AutoSave(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Save();
    }

    private void Save()
    {
        if (_loading) return;

        // Don't save if league list hasn't loaded yet
        if (LeagueCombo.Items.Count == 0) return;

        try
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (configDir is null) return;
            _ = Directory.CreateDirectory(configDir);

            var existingJson = "{}";
            if (File.Exists(_configPath))
                existingJson = File.ReadAllText(_configPath, Encoding.UTF8);

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
                pricing["AutoPriceThresholds"] = AutoThresholdsCheck.IsChecked == true;
                pricing["League"] = LeagueCombo.Text;
                pricing["DisplayCurrency"] = CurrencyExaltCheck.IsChecked == true ? "exalt" : "chaos";

                // Only save threshold values if they parse and satisfy Red < Orange < Green.
                // Otherwise the previous valid values remain in the config.
                var redOk = decimal.TryParse(RedThresholdBox.Text, out var red);
                var orangeOk = decimal.TryParse(OrangeThresholdBox.Text, out var orange);
                var greenOk = decimal.TryParse(GreenThresholdBox.Text, out var green);
                var thresholdsValid = redOk && orangeOk && greenOk && red < orange && orange < green;

                if (thresholdsValid)
                {
                    pricing["RedThreshold"] = red;
                    pricing["OrangeThreshold"] = orange;
                    pricing["GreenThreshold"] = green;
                    ValidationError.Text = "";
                }
                else
                {
                    ValidationError.Text = "Thresholds must be: Red < Orange < Green.";
                }
            }

            if (rootObj["OCR"] is JsonObject ocr)
            {
                ocr["DebugOverlay"] = DebugOverlayCheck.IsChecked == true;
                ocr["HideDebugOverlayWhenInterfaceNotDetected"] = HideDebugOverlayCheck.IsChecked == true;
            }

            if (rootObj["Update"] is JsonObject update)
                update["AutoUpdate"] = AutoUpdateCheck.IsChecked == true;

            var json = rootObj.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(_configPath, json + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            ValidationError.Text = $"Save failed: {ex.Message}";
        }
    }

    private void PopulateLogLevelCombo()
    {
        LogLevelCombo.Items.Clear();
        _ = LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Trace", Tag = "Trace" });
        _ = LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Debug", Tag = "Debug" });
        _ = LogLevelCombo.Items.Add(new ComboBoxItem { Content = "Information", Tag = "Information" });
        LogLevelCombo.SelectedIndex = 2; // Information
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCACTIVATE = 0x0086;

        if (msg == WM_NCACTIVATE)
        {
            handled = true;
            return 1;
        }

        return IntPtr.Zero;
    }

    private static readonly SolidColorBrush HeaderFooterHover = new(Color.FromRgb(0x2A, 0x2E, 0x38));
    private static readonly SolidColorBrush ContentHover = new(Color.FromRgb(0x22, 0x27, 0x2E));

    private void Section_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = border.Name == "ContentSection" ? ContentHover : HeaderFooterHover;
        }
    }

    private void Section_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = border.Name == "ContentSection"
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x2E)); // SurfaceAltBrush
        }
    }

    private void CurrencyChaos_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        CurrencyExaltCheck.IsChecked = false;
        AutoSave(sender, e);
    }

    private void CurrencyChaos_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (CurrencyExaltCheck.IsChecked != true)
            CurrencyChaosCheck.IsChecked = true;
        AutoSave(sender, e);
    }

    private void CurrencyExalt_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        CurrencyChaosCheck.IsChecked = false;
        AutoSave(sender, e);
    }

    private void CurrencyExalt_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (CurrencyChaosCheck.IsChecked != true)
            CurrencyExaltCheck.IsChecked = true;
        AutoSave(sender, e);
    }

    private void AutoThresholds_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateThresholdVisibility();
        AutoSave(sender, e);
    }

    private void UpdateThresholdVisibility()
    {
        ThresholdBoxes.Visibility = AutoThresholdsCheck.IsChecked == true
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
