using System.Windows.Threading;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed class DashboardService : IDisposable
{
    public static Action<IProgress<int>>? UpdateTrigger { get; set; }

    private readonly DashboardLogSink _sink;
    private Thread? _wpfThread;
    private DashboardWindow? _window;
    private readonly ManualResetEventSlim _windowReady = new();
    private volatile Action? _onWindowClosed;
    private volatile Action? _onWindowLoaded;

    public Func<IProgress<int>, Task>? OnUpdateRequested { get; set; }

    public DashboardService(DashboardLogSink sink)
    {
        _sink = sink;
    }

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

        _windowReady.Wait(TimeSpan.FromSeconds(10));
    }

    private void RunWpfApp()
    {
        var app = new System.Windows.Application();

        app.DispatcherUnhandledException += (_, e) =>
        {
            _sink.Emit($"Dashboard error: {e.Exception.Message}", "red");
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var msg = e.ExceptionObject is Exception ex ? ex.Message : e.ExceptionObject.ToString();
            _sink.Emit($"Fatal error: {msg}", "red");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _sink.Emit($"Task error: {e.Exception.InnerException?.Message ?? e.Exception.Message}", "red");
            e.SetObserved();
        };

        _window = new DashboardWindow(_sink);

        _window.SetUpdateTrigger(p => UpdateTrigger?.Invoke(p));

        _window.Closed += (_, _) =>
        {
            _onWindowClosed?.Invoke();
            app.Shutdown();
        };

        _window.Loaded += (_, _) => _onWindowLoaded?.Invoke();

        _windowReady.Set();
        app.Run(_window);
    }

    public void Stop()
    {
        try
        {
            _window?.Dispatcher.InvokeAsync(_window.Close, DispatcherPriority.Send);
        }
        catch (TaskCanceledException) { }
    }

    public void SetStatus(string text, string color)
    {
        _window?.Dispatcher.InvokeAsync(() => _window.SetStatus(text, color));
    }

    public void SetOnSetupContinue(Action callback)
    {
        _window?.Dispatcher.InvokeAsync(() => _window.SetOnSetupContinue(callback));
    }

    public void ShowSetupPrompt()
    {
        _window?.Dispatcher.InvokeAsync(_window.ShowSetupPrompt);
    }

    public void HideSetupPrompt()
    {
        _window?.Dispatcher.InvokeAsync(_window.HideSetupPrompt);
    }

    public void ShowUpdateButton()
    {
        _window?.Dispatcher.InvokeAsync(_window.ShowUpdateButton);
    }

    public void HideUpdateButton()
    {
        _window?.Dispatcher.InvokeAsync(_window.HideUpdateButton);
    }

    public void ShowUpdateOverlay()
    {
        _window?.Dispatcher.InvokeAsync(_window.ShowUpdateOverlay);
    }

    public void HideUpdateOverlay()
    {
        _window?.Dispatcher.InvokeAsync(_window.HideUpdateOverlay);
    }

    public void SetUpdateProgress(int percent)
    {
        _window?.Dispatcher.InvokeAsync(() => _window.SetUpdateProgress(percent));
    }

    public void SetReRunSetupTrigger(Action trigger)
    {
        _window?.Dispatcher.InvokeAsync(() => _window.SetReRunSetupTrigger(trigger));
    }

    public void Dispose()
    {
        _windowReady.Dispose();
    }
}
