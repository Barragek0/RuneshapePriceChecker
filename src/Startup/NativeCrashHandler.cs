using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.Startup;

// Native crash handler using a pure native VEH DLL (bypasses the .NET runtime
// limitation that blocks managed delegates from vectored exception handlers).
internal static class NativeCrashHandler
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        try
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            _ = Directory.CreateDirectory(logsDir);
            RegisterNativeCrashHandler(logsDir);
        }
        catch
        {
            // Native DLL may not be deployed (e.g. during development) — skip.
        }
    }

    [DllImport("NativeCrashHandler.dll", CharSet = CharSet.Unicode,
        EntryPoint = "RegisterCrashHandler")]
    private static extern void RegisterNativeCrashHandler(string logDirectory);
}
