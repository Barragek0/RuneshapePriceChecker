using System.Windows.Threading;

namespace RuneshapePriceChecker.Tests;

public static class StaTestHelper
{
    private static readonly object _lock = new();
    private static Dispatcher? _dispatcher;

    private static void EnsureDispatcher()
    {
        if (_dispatcher is not null) return;
        lock (_lock)
        {
            if (_dispatcher is not null) return;
            using var ready = new ManualResetEventSlim(false);
            Dispatcher? d = null;
            var thread = new Thread(() =>
            {
                d = Dispatcher.CurrentDispatcher;
                if (System.Windows.Application.Current is null)
                    _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "WpfTestDispatcher"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            _dispatcher = d!;
        }
    }

    public static void RunOnStaThread(Action action)
    {
        EnsureDispatcher();
        Exception? exception = null;
        _dispatcher!.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { exception = ex; }
        });
        if (exception is not null)
            throw new InvalidOperationException($"Test failed on STA thread: {exception.Message}", exception);
    }
}
