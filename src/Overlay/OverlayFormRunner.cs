using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Overlay;

internal static class OverlayFormRunner
{
    public static Thread Start<T>(
        string threadName,
        object sync,
        Action<T?> setForm,
        ILogger? logger = null,
        string? timeoutWarning = null)
        where T : Form, new()
    {
        T? localForm = null;
        var thread = new Thread(() =>
        {
            using var f = new T();
            _ = f.Handle;
            lock (sync) { setForm(f); localForm = f; Monitor.PulseAll(sync); }
            Application.Run(f);
            lock (sync) setForm(null);
        })
        {
            IsBackground = true,
            Name = threadName
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        lock (sync)
        {
            while (localForm is null)
                if (!Monitor.Wait(sync, TimeSpan.FromSeconds(5)))
                {
                    logger?.LogWarning(timeoutWarning ?? $"{typeof(T).Name} STA form timed out");
                    setForm(null);
                    return thread;
                }
        }

        return thread;
    }
}
