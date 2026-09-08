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
_ = SetErrorMode(0x0002 | 0x0020);

CrashLogger.PrepareSession();
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    CrashLogger.WriteCrash("CrashSimulator unhandled exception", eventArgs.ExceptionObject as Exception);
};
var logPath = Path.Combine(SentryPaths.RootDirectory, "simulator-log.txt");
var contextPath = Path.Combine(SentryPaths.RootDirectory, "simulator-context.json");
Directory.CreateDirectory(SentryPaths.RootDirectory);
File.WriteAllText(logPath, "CrashSimulator diagnostic log\n");
File.WriteAllText(contextPath, "{\"simulator\":true}\n");
var dsn = Environment.GetEnvironmentVariable("SENTRY_TEST_DSN") ?? string.Empty;
var reporter = SentryCrashReporter.Start(new SentryCrashReporterOptions(
    !string.IsNullOrWhiteSpace(dsn), dsn, "crash-simulator@1.0.0", "test",
    SentryPaths.DatabaseDirectory,
    SentryPaths.HandlerPath,
    logPath, logPath + ".previous", CrashLogger.CurrentManagedCrashPath, contextPath));

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

    case "managed-unhandled":
        throw new InvalidOperationException("CrashSimulator managed unhandled exception");

    case "handled":
        try { Marshal.ReadByte(IntPtr.Zero); }
        catch (AccessViolationException) { Console.Error.WriteLine("Handled access violation"); }
        reporter.Dispose();
        return 0;

    case "no-crash":
        reporter.Dispose();
        return 0;

    default:
        Console.Error.WriteLine("Usage: CrashSimulator <crash-type>");
        Console.Error.WriteLine("  crash-type: av-read | av-write | stack");
        return 1;
}

Console.Error.WriteLine("ERROR: No crash occurred (unexpected)");
reporter.Dispose();
return 1;

[DllImport("wer.dll")]
static extern int WerAddExcludedApplication(string exeName, bool allUsers);

[DllImport("kernel32.dll")]
static extern uint SetErrorMode(uint mode);

[MethodImpl(MethodImplOptions.NoInlining)]
static void Recurse(int depth)
{
    Span<byte> stackFrame = stackalloc byte[512];
    stackFrame[0] = (byte)(depth & 0xFF);
    Recurse(depth + 1);
}
