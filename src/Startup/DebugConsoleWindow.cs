using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.Startup;

internal static partial class DebugConsoleWindow
{
    public static void TryOpen()
    {
        if (GetConsoleWindow() == IntPtr.Zero && AllocConsole())
        {
            Console.Title = "RuneshapePriceChecker Console";
            Console.WriteLine("Console attached.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
