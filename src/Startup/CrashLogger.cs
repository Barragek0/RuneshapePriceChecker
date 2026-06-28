using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Startup;

internal static class CrashLogger
{
    private static readonly string LogDir = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly object SyncLock = new();
    private static volatile bool _hasCrashed;

    internal static volatile LogLevel MinimumLogLevel = LogLevel.Information;

    public static string GenerateCrashLogPath()
    {
        _ = Directory.CreateDirectory(LogDir);
        return Path.Combine(LogDir, $"{DateTime.Now:yyyyMMdd-HHmmss.fff}-crash.txt");
    }

    public static string GenerateCaughtLogPath()
    {
        _ = Directory.CreateDirectory(LogDir);
        return Path.Combine(LogDir, $"{DateTime.Now:yyyyMMdd-HHmmss.fff}-caught.txt");
    }

    private static void WriteLog(string path, string title, string reportLabel, Exception? ex, [CallerFilePath] string? sourceFile = null, [CallerMemberName] string? caller = null)
    {
        try
        {
            var sb = new StringBuilder(4096);
            _ = sb.AppendLine("========================================");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"RuneshapePriceChecker {reportLabel}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Version:   {Assembly.GetEntryAssembly()?.GetName()?.Version?.ToString() ?? "unknown"}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"PID:       {Environment.ProcessId}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Process:   {Environment.ProcessPath}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"OS:        {Environment.OSVersion}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"x64:       {Environment.Is64BitProcess}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"GC:        {System.Runtime.GCSettings.IsServerGC}");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Title:     {title}");
            if (sourceFile is not null && caller is not null)
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Source:    {Path.GetFileName(sourceFile)}::{caller}");
            _ = sb.AppendLine("========================================");
            _ = sb.AppendLine();

            if (ex is not null)
            {
                _ = sb.AppendLine("--- EXCEPTION ---");
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Type:    {ex.GetType().FullName}");
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Message: {ex.Message}");
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"HResult: 0x{ex.HResult:X8}");
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Source:  {ex.Source}");
                _ = sb.AppendLine();
                _ = sb.AppendLine("--- STACK TRACE ---");
                _ = sb.AppendLine(ex.ToString());
                _ = sb.AppendLine();

                if (ex is AggregateException ae)
                {
                    for (var i = 0; i < ae.InnerExceptions.Count; i++)
                    {
                        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"--- Inner Exception [{i}] ---");
                        _ = sb.AppendLine(ae.InnerExceptions[i].ToString());
                        _ = sb.AppendLine();
                    }
                }
                else if (ex.InnerException is not null)
                {
                    _ = sb.AppendLine("--- INNER EXCEPTION ---");
                    _ = sb.AppendLine(ex.InnerException.ToString());
                    _ = sb.AppendLine();
                }
            }

            _ = sb.AppendLine("--- SYSTEM INFO ---");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"TickCount:   {Environment.TickCount64} ms");
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"CLR:         {RuntimeInformation.FrameworkDescription}");

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Best effort
        }
    }

    public static void WriteCrash(string title, Exception? ex, [CallerFilePath] string? sourceFile = null, [CallerMemberName] string? caller = null)
    {
        if (_hasCrashed) return;
        lock (SyncLock)
        {
            if (_hasCrashed) return;
            _hasCrashed = true;
            _ = Directory.CreateDirectory(LogDir);
            WriteLog(GenerateCrashLogPath(), title, "Crash Report", ex, sourceFile, caller);
        }
    }

    public static void WriteCaught(string title, Exception? ex, [CallerFilePath] string? sourceFile = null, [CallerMemberName] string? caller = null)
    {
        _ = Directory.CreateDirectory(LogDir);
        WriteLog(GenerateCaughtLogPath(), title, "Caught Exception Report", ex, sourceFile, caller);
    }
}
