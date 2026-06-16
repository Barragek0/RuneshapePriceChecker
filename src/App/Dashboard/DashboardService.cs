using System.Windows.Threading;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed class DashboardService(DashboardLogSink sink) : IDisposable
{
    public static Action<IProgress<int>>? UpdateTrigger { get; set; }
    private static readonly ManualResetEventSlim ChangelogDismissedEvent = new(false);
    public static Task WaitForChangelogDismissedAsync(CancellationToken ct)
    {
        return Task.Run(() => ChangelogDismissedEvent.Wait(ct), ct);
    }

    private readonly DashboardLogSink _sink = sink;
    private Thread? _wpfThread;
    private DashboardWindow? _window;
    private readonly ManualResetEventSlim _windowReady = new();
    private volatile Action? _onWindowClosed;
    private volatile Action? _onWindowLoaded;

    public void SetOnWindowClosed(Action callback)
    {
        _onWindowClosed = callback;
    }

    public void SetOnWindowLoaded(Action callback)
    {
        _onWindowLoaded = callback;
    }

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
            _sink.Emit($"Dashboard error: {e.Exception.GetType().Name}: {e.Exception.Message}", "red");
            _ = _window?.Dispatcher.InvokeAsync(() => _window.SetStatus($"Error: {e.Exception.Message}", "red"))!;
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var msg = e.ExceptionObject?.ToString() ?? "Unknown fatal error";
            var type = e.ExceptionObject?.GetType().Name ?? "Unknown";
            _sink.Emit($"Fatal error: {type}: {msg}", "red");
            _ = _window?.Dispatcher.InvokeAsync(() => _window.SetStatus($"Fatal: {msg}", "red"))!;
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var inner = e.Exception.InnerException;
            var type = inner?.GetType().Name ?? e.Exception.GetType().Name;
            var msg = inner?.Message ?? e.Exception.Message;
            _sink.Emit($"Task error: {type}: {msg}", "red");
            _ = _window?.Dispatcher.InvokeAsync(() => _window.SetStatus($"Error: {msg}", "red"))!;
            e.SetObserved();
        };

        _window = new DashboardWindow(_sink);

        _window.SetUpdateTrigger(p => UpdateTrigger?.Invoke(p));

        _window.ChangelogShown += ChangelogDismissedEvent.Reset;
        _window.ChangelogDismissed += ChangelogDismissedEvent.Set;

        _window.Loaded += (_, _) =>
        {
            _onWindowLoaded?.Invoke();
            if (!_window.IsChangelogVisible)
                ChangelogDismissedEvent.Set();
        };

        _window.Closed += (_, _) =>
        {
            _onWindowClosed?.Invoke();
            app.Shutdown();
        };

        _windowReady.Set();
        _ = app.Run(_window);
    }

    public void Stop()
    {
        try
        {
            _ = (_window?.Dispatcher.InvokeAsync(_window.Close, DispatcherPriority.Send));
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
        _ = (_window?.Dispatcher.InvokeAsync(() => _window.SetStatus(text, color)));
    }

    public void LogError(string message)
    {
        _ = (_window?.Dispatcher.InvokeAsync(() => _window.LogError(message)));
    }

    public void SetOnSetupContinue(Action callback)
    {
        _ = (_window?.Dispatcher.InvokeAsync(() => _window.SetOnSetupContinue(callback)));
    }

    public void ShowSetupPrompt()
    {
        _ = (_window?.Dispatcher.InvokeAsync(_window.ShowSetupPrompt));
    }

    public void HideSetupPrompt()
    {
        _ = (_window?.Dispatcher.InvokeAsync(_window.HideSetupPrompt));
    }

    public void ShowUpdateButton()
    {
        _ = (_window?.Dispatcher.InvokeAsync(_window.ShowUpdateButton));
    }

    public void HideUpdateButton()
    {
        _ = (_window?.Dispatcher.InvokeAsync(_window.HideUpdateButton));
    }

    public void ShowUpdateOverlay()
    {
        _ = (_window?.Dispatcher.InvokeAsync(_window.ShowUpdateOverlay));
    }

    public void HideUpdateOverlay()
    {
        _ = (_window?.Dispatcher.InvokeAsync(_window.HideUpdateOverlay));
    }

    public void BringToFront()
    {
        _ = (_window?.Dispatcher.InvokeAsync(_window.BringToFront));
    }

    public void SetUpdateProgress(int percent)
    {
        _ = (_window?.Dispatcher.InvokeAsync(() => _window.SetUpdateProgress(percent)));
    }

    public void SetReRunSetupTrigger(Action trigger)
    {
        _ = (_window?.Dispatcher.InvokeAsync(() => _window.SetReRunSetupTrigger(() =>
        {
            ResetInitialSetupComplete();
            trigger();
        })));
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
            var windowNode = root["Window"] as System.Text.Json.Nodes.JsonObject ?? [];
            windowNode["InitialSetupComplete"] = false;
            root["Window"] = windowNode;
            File.WriteAllText(configPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch { }
    }

    public void Dispose()
    {
        _windowReady.Dispose();
    }
}
