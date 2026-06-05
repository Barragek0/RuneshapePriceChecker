using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.Startup;

internal static class ConsoleCloseHandler
{
    private delegate bool HandlerRoutine(uint dwCtrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine? handler, bool add);

    public static void Register()
    {
        SetConsoleCtrlHandler(_ => { Environment.Exit(0); return true; }, true);
    }
}
