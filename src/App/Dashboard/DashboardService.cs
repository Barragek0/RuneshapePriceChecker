using Microsoft.Extensions.Logging;
using System.Windows.Threading;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Pricing;
using RuneshapePriceChecker.Startup;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed class DashboardService(DashboardLogSink sink, DebugMetricsCollector? metrics = null,
    ILogger<DashboardWindow>? dashboardLogger = null) : IDisposable
{
    public DebugMetricsCollector? Metrics { get; } = metrics;
    public static Action<IProgress<int>>? UpdateTrigger { get; set; }
    private static volatile bool _isUpdating;
    public static bool IsUpdating { get => _isUpdating; set => _isUpdating = value; }
    public static bool CanSaveSettings => !IsUpdating;
    private static readonly ManualResetEventSlim ChangelogDismissedEvent = new(false);
    public static Task WaitForChangelogDismissedAsync(CancellationToken ct)
    {
        return Task.Run(() => ChangelogDismissedEvent.Wait(ct), ct);
    }

    private Thread? _wpfThread;
    public DashboardWindow? Window { get; private set; }
    private readonly ManualResetEventSlim _windowReady = new();
    private volatile Action? _onWindowClosed;
    private volatile Action? _onWindowLoaded;

    public void SetOnWindowClosed(Action callback) => _onWindowClosed = callback;

    public void SetOnWindowLoaded(Action callback) => _onWindowLoaded = callback;

    public void Start()
    {
        _wpfThread = new Thread(RunWpfApp)
        {
            Name = "Dashboard",
            IsBackground = true
        };
        _wpfThread.SetApartmentState(ApartmentState.STA);
        _wpfThread.Start();

        _ = _windowReady.Wait(TimeSpan.FromSeconds(10));
    }

    private void RunWpfApp()
    {
        var app = new System.Windows.Application();

        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLogger.WriteCaught($"WPF dispatcher exception ({e.Exception.GetType().Name})", e.Exception);
            sink.Emit($"Dashboard error: {e.Exception.GetType().Name}: {e.Exception.Message}", "red");
            _ = Window?.Dispatcher.InvokeAsync(() => Window.SetStatus($"Error: {e.Exception.Message}", "red"))!;
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var msg = e.ExceptionObject?.ToString() ?? "Unknown fatal error";
            var type = e.ExceptionObject?.GetType().Name ?? "Unknown";
            sink.Emit($"Fatal error: {type}: {msg}", "red");
            _ = Window?.Dispatcher.InvokeAsync(() => Window.SetStatus($"Fatal: {msg}", "red"))!;
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var inner = e.Exception.InnerException;
            var type = inner?.GetType().Name ?? e.Exception.GetType().Name;
            var msg = inner?.Message ?? e.Exception.Message;
            sink.Emit($"Task error: {type}: {msg}", "red");
            _ = Window?.Dispatcher.InvokeAsync(() => Window.SetStatus($"Error: {msg}", "red"))!;
            e.SetObserved();
        };

        Window = new DashboardWindow(sink, Metrics, dashboardLogger);


        // Auto-detect OCR language from the game's config file so the app
        // picks up language changes made in-game since the last launch.
        Poe2ConfigFile.StartWatching();
        var gameLang = Poe2ConfigFile.Language;
        if (gameLang is not null)
            Window.SetGameLanguage(gameLang, ItemNameTranslator.IsLanguageSupported(gameLang));
        Poe2ConfigFile.ConfigChanged += () =>
        {
            var effective = Poe2ConfigFile.Language ?? "eng";
            var current = Window.GameLanguage;
            if (!string.Equals(effective, current, StringComparison.OrdinalIgnoreCase))
            {
                sink.Emit("[Config] PoE2 game language changed — updating OCR language.");
                _ = Window.Dispatcher.InvokeAsync(() => Window.SetGameLanguage(effective, ItemNameTranslator.IsLanguageSupported(effective)));
            }
        };

        Window.SetUpdateTrigger(p => UpdateTrigger?.Invoke(p));

        Window.ChangelogShown += ChangelogDismissedEvent.Reset;
        Window.ChangelogDismissed += ChangelogDismissedEvent.Set;

        Window.Loaded += (_, _) =>
        {
            _onWindowLoaded?.Invoke();
            if (!Window.IsChangelogVisible)
                ChangelogDismissedEvent.Set();
        };

        Window.Closed += (_, _) =>
        {
            _onWindowClosed?.Invoke();
            app.Shutdown();
        };

        _windowReady.Set();
        _ = app.Run(Window);

    }

    public void Stop()
    {
        try
        {
            _ = (Window?.Dispatcher.InvokeAsync(Window.Close, DispatcherPriority.Send));
        }
        catch (TaskCanceledException) { }
    }

    private string _lastStatusText = "";
    private string _lastStatusColor = "";

    public void SetStatus(string text, string color)
    {
        if (text == _lastStatusText && color == _lastStatusColor)
            return;
        _lastStatusText = text;
        _lastStatusColor = color;
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.SetStatus(text, color)));
    }

    public void LogError(string message)
    {
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.LogError(message)));
    }

    public void SetOnSetupContinue(Action callback)
    {
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.SetOnSetupContinue(callback)));
    }

    public void ShowSetupPrompt()
    {
        _ = (Window?.Dispatcher.InvokeAsync(Window.ShowSetupPrompt));
    }

    public void HideSetupPrompt()
    {
        _ = (Window?.Dispatcher.InvokeAsync(Window.HideSetupPrompt));
    }

    public void ShowUpdateButton()
    {
        _ = (Window?.Dispatcher.InvokeAsync(Window.ShowUpdateButton));
    }

    public void HideUpdateButton()
    {
        _ = (Window?.Dispatcher.InvokeAsync(Window.HideUpdateButton));
    }

    public void ShowUpdateOverlay()
    {
        _ = (Window?.Dispatcher.InvokeAsync(Window.ShowUpdateOverlay));
    }

    public void HideUpdateOverlay()
    {
        _ = (Window?.Dispatcher.InvokeAsync(Window.HideUpdateOverlay));
    }

    public void BringToFront()
    {
        _ = (Window?.Dispatcher.InvokeAsync(Window.BringToFront));
    }

    public void SetUpdateProgress(int percent)
    {
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.SetUpdateProgress(percent)));
    }

    public void SetReRunSetupTrigger(Action trigger)
    {
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.SetReRunSetupTrigger(() =>
        {
            ResetInitialSetupComplete();
            trigger();
        })));
    }

    public void SetBugReportTrigger(Action trigger)
    {
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.SetBugReportTrigger(trigger)));
    }

    public void SetOnBugReportContinue(Action callback)
    {
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.SetOnBugReportContinue(callback)));
    }

    public void SetOnBugReportDone(Action callback)
    {
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.SetOnBugReportDone(callback)));
    }

    public void SetOnBugReportCancel(Action callback)
    {
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.SetOnBugReportCancel(callback)));
    }

    public void ShowBugReportPrompt()
    {
        _ = (Window?.Dispatcher.InvokeAsync(Window.ShowBugReportPrompt));
    }

    public void ShowBugReportDataCollected(int fileCount, string zipFileName)
    {
        _ = (Window?.Dispatcher.InvokeAsync(() => Window.ShowBugReportDataCollected(fileCount, zipFileName)));
    }

    public void HideBugReportAll()
    {
        _ = (Window?.Dispatcher.InvokeAsync(Window.HideBugReportAll));
    }

    private static void ResetInitialSetupComplete()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
            if (!File.Exists(configPath)) return;
            var json = File.ReadAllText(configPath, System.Text.Encoding.UTF8);
            var root = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (root is null) return;
            var windowNode = root[nameof(Window)] as System.Text.Json.Nodes.JsonObject ?? [];
            windowNode["InitialSetupComplete"] = false;
            root[nameof(Window)] = windowNode;
            File.WriteAllText(configPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch { }
    }

    public void Dispose()
    {
        sink.Dispose();
        _windowReady.Dispose();
    }
}
