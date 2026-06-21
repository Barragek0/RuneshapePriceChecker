using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32;

namespace RuneshapePriceChecker.App.Dashboard;

[SupportedOSPlatform("windows")]
internal static class RpcServiceRunner
{
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "RuneshapePriceChecker";
    private const string ExitEventName = "Global\\RuneshapePriceChecker_ExitService";
    private const string ServiceActiveEventName = "Global\\RuneshapePriceChecker_ServiceActive";

    public static void Register()
    {
        KillExistingService();
        SignalExit();
        for (var i = 0; i < 10; i++)
        {
            if (!IsServiceRunning()) break;
            Thread.Sleep(200);
        }

        var exePath = Environment.ProcessPath!;
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
        key?.SetValue(RegistryValueName, $"\"{exePath}\" --rpcservice");

        var psi = new ProcessStartInfo(exePath, "--rpcservice")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(psi);
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
        key?.DeleteValue(RegistryValueName, throwOnMissingValue: false);
        SignalExit();
    }

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey);
            return key?.GetValue(RegistryValueName) is not null;
        }
    }

    public static bool IsRunning() => IsServiceRunning();

    public static void SignalExit()
    {
        try
        {
            using var evt = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
            _ = evt.Set();
        }
        catch { }
    }

    public static void Run()
    {
        using var activeEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ServiceActiveEventName);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
        var selfId = Environment.ProcessId;

        // Watch all process creations, then check for PoE2 by window title (more reliable than process names)
        var query = "SELECT * FROM __InstanceCreationEvent WITHIN 2 " +
                    "WHERE TargetInstance ISA 'Win32_Process'";

        using var watcher = new ManagementEventWatcher(query);
        watcher.EventArrived += (_, _) =>
        {
            try
            {
                // Don't launch if main app is already running
                foreach (var p in Process.GetProcessesByName("RuneshapePriceChecker"))
                {
                    try { if (p.Id != selfId) return; }
                    finally { p.Dispose(); }
                }

                // Retry a few times with short delays so PoE2 has time to set its window title
                for (var retry = 0; retry < 6; retry++)
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero &&
                                !string.IsNullOrWhiteSpace(p.MainWindowTitle) &&
                                p.MainWindowTitle.Equals("Path of Exile 2", StringComparison.OrdinalIgnoreCase))
                            {
                                var exePath = Environment.ProcessPath;
                                if (exePath is null) return;
                                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                                return;
                            }
                        }
                        finally { p.Dispose(); }
                    }
                    Thread.Sleep(500);
                }
            }
            catch { }
        };
        watcher.Start();

        exitEvent.WaitOne();
        watcher.Stop();
    }

    private static bool IsServiceRunning()
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(ServiceActiveEventName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void KillExistingService()
    {
        var selfId = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("RuneshapePriceChecker"))
        {
            if (p.Id == selfId) { p.Dispose(); continue; }
            try { p.Kill(); } catch { }
            p.Dispose();
        }
    }
}
