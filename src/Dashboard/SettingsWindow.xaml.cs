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
                RedThresholdBox.Text = pricing["RedThreshold"]?.GetValue<decimal>().ToString(CultureInfo.InvariantCulture) ?? "0.5";
                OrangeThresholdBox.Text = pricing["OrangeThreshold"]?.GetValue<decimal>().ToString(CultureInfo.InvariantCulture) ?? "1.0";
                GreenThresholdBox.Text = pricing["GreenThreshold"]?.GetValue<decimal>().ToString(CultureInfo.InvariantCulture) ?? "5.0";
            }

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

    private void Save_Click(object sender, RoutedEventArgs e)
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
            }

            if (rootObj["Update"] is JsonObject update)
                update["AutoUpdate"] = AutoUpdateCheck.IsChecked == true;

            var json = rootObj.ToJsonString(new() { WriteIndented = true });
            File.WriteAllText(_configPath, json + Environment.NewLine, Encoding.UTF8);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ValidationError.Text = $"Save failed: {ex.Message}";
        }
    }

    private void ShowValidation(string message, Control target)
    {
        ValidationError.Text = message;
        target.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        target.BorderThickness = new Thickness(1);
        _ = target.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
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
}
