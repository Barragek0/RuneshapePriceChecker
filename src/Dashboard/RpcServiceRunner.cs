using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RuneshapePriceChecker.App.Dashboard;

[SupportedOSPlatform("windows")]
internal static class RpcServiceRunner
{
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "RuneshapePriceChecker";
    private const string ExitEventName = "Global\\RuneshapePriceChecker_ExitService";
    private const string ServiceActiveEventName = "Global\\RuneshapePriceChecker_ServiceActive";
    internal const string CloseByPoe2EventName = "Global\\RuneshapePriceChecker_CloseByPoe2";

    public static void Register()
    {
        KillExistingService();
        SignalExit();
        // Wait up to 5s for the kernel event handle to be cleaned up by the OS
        for (var i = 0; i < 25; i++)
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

    private static volatile bool _appLaunched;
    private static volatile int _lastPoe2Pid;
    private static volatile bool _hasPoe2Pid;

    public static void Run()
    {
        try
        {
            using var activeEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ServiceActiveEventName);
            using var exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
            using var closeByPoe2Event = new EventWaitHandle(false, EventResetMode.ManualReset, CloseByPoe2EventName);
            var selfId = Environment.ProcessId;
            _appLaunched = false;

            // Watch all process creations, then check for PoE2 by window title (more reliable than process names)
            var query = "SELECT * FROM __InstanceCreationEvent WITHIN 1 " +
                        "WHERE TargetInstance ISA 'Win32_Process'";

            using var watcher = new ManagementEventWatcher(query);
            watcher.EventArrived += (_, _) =>
            {
                try
                {
                    // When the app was launched by us, check if it has exited and why
                    if (_appLaunched)
                    {
                        var appRunning = false;
                        foreach (var p in Process.GetProcessesByName("RuneshapePriceChecker"))
                        {
                            try { if (p.Id != selfId) { appRunning = true; break; } }
                            finally { p.Dispose(); }
                        }

                        if (!appRunning)
                        {
                            // App exited. Was it CloseWithPoE2? (main app signals before CloseWithPoE2 kill)
                            if (closeByPoe2Event.WaitOne(0))
                            {
                                // CloseWithPoE2 — reset so next PoE2 session re-launches
                                _appLaunched = false;
                                _hasPoe2Pid = false;
                                closeByPoe2Event.Reset();
                            }
                            else
                            {
                                // Manual close (or crash). Check if PoE2 PID changed (new session) or exited.
                                var currentPid = 0;
                                foreach (var p in Process.GetProcesses())
                                {
                                    try
                                    {
                                        if (p.MainWindowHandle != IntPtr.Zero &&
                                            !string.IsNullOrWhiteSpace(p.MainWindowTitle) &&
                                            p.MainWindowTitle.Equals("Path of Exile 2", StringComparison.OrdinalIgnoreCase))
                                        {
                                            currentPid = p.Id;
                                            break;
                                        }
                                    }
                                    finally { p.Dispose(); }
                                }

                                if (currentPid == 0)
                                {
                                    // PoE2 not running — session ended, reset
                                    _appLaunched = false;
                                    _hasPoe2Pid = false;
                                }
                                else if (_hasPoe2Pid && currentPid != _lastPoe2Pid)
                                {
                                    // PoE2 PID changed — new session (exited and restarted), reset
                                    _appLaunched = false;
                                    _lastPoe2Pid = currentPid;
                                }
                                else if (!_hasPoe2Pid)
                                {
                                    // First time seeing PoE2 PID — cache it
                                    _lastPoe2Pid = currentPid;
                                    _hasPoe2Pid = true;
                                }
                                // else: same PID, same session — keep _appLaunched
                            }
                        }
                    }

                    // Once per PoE2 session: don't re-launch if user closed the app manually
                    if (_appLaunched) return;

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
                                    _appLaunched = true;
                                    _lastPoe2Pid = p.Id;
                                    _hasPoe2Pid = true;
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RpcServiceRunner.Run failed: {ex.GetType().Name}: {ex.Message}");
        }
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

    internal static void KillExistingService()
    {
        var selfId = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("RuneshapePriceChecker"))
        {
            if (p.Id == selfId) { p.Dispose(); continue; }
            try
            {
                p.Kill();
                if (!p.WaitForExit(3000))
                    System.Diagnostics.Debug.WriteLine($"RpcServiceRunner: PID {p.Id} did not exit within 3s of Kill");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RpcServiceRunner: Kill PID {p.Id} failed: {ex.Message}");
            }
            p.Dispose();
        }
    }
}
