using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuneshapePriceChecker.Startup;

var crashType = args.Length > 0 ? args[0].ToLowerInvariant() : "";

Console.Error.WriteLine($"CrashSimulator: triggering '{crashType}'");
Console.Error.WriteLine($"Base: {AppContext.BaseDirectory}");
Console.Error.WriteLine($"Logs: {Path.Combine(AppContext.BaseDirectory, "logs")}");

// Suppress Windows Error Reporting dialog so the process exits cleanly
// on crash instead of showing a "has stopped working" dialog.
// This is critical for automated testing — without it, WER hangs the process.
try { _ = WerAddExcludedApplication(AppDomain.CurrentDomain.FriendlyName, false); }
catch (EntryPointNotFoundException) { /* Windows version may not have this API */ }
Environment.SetEnvironmentVariable("DOTNET_EnableCrashReport", "0");

NativeCrashHandler.Register();

// Flush so all output up to the crash point is visible
Console.Error.Flush();

switch (crashType)
{
    case "access-violation-read":
    case "av-read":
        Marshal.ReadByte(IntPtr.Zero);
        break;

    case "access-violation-write":
    case "av-write":
        unsafe { *(int*)0 = 42; }
        break;

    case "stack-overflow":
    case "stack":
        Recurse(0);
        break;

    default:
        Console.Error.WriteLine("Usage: CrashSimulator <crash-type>");
        Console.Error.WriteLine("  crash-type: av-read | av-write | stack");
        return 1;
}

// Should never reach here for crash types
Console.Error.WriteLine("ERROR: No crash occurred (unexpected)");
return 1;

[DllImport("kernel32.dll")]
static extern int WerAddExcludedApplication(string exeName, bool allUsers);

[MethodImpl(MethodImplOptions.NoInlining)]
static void Recurse(int depth)
{
    Span<byte> stackFrame = stackalloc byte[512];
    stackFrame[0] = (byte)(depth & 0xFF);
    Recurse(depth + 1);
}
