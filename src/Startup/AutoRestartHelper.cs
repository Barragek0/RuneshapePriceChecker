using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RuneshapePriceChecker.Startup;

internal static class AutoRestartHelper
{
    private static Process? _watchdogProcess;
    private static ILogger? _logger;

    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string? _watchdogScriptPath;

    public static void StartWatchdog()
    {
        if (_watchdogProcess is { HasExited: false })
        {
            _logger?.LogTrace("AutoRestart: watchdog already running, skipping");
            return;
        }

        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        var parentPid = Environment.ProcessId;
        _logger?.LogTrace("AutoRestart: starting watchdog for {Exe} (parent PID={Pid})", exePath, parentPid);

        // Write the watchdog script to a temp file to avoid PowerShell -Command
        // quoting issues (nested double-quotes break the command line parser).
        // Pass the log directory, not a pre-computed path — the app creates its log
        // file later during host init, so we need to find it dynamically at write time.
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"rpc-watchdog-{parentPid}.ps1");
        _watchdogScriptPath = scriptPath;

        var script = $$"""
        $parentPid = {{parentPid}}
        $exePath = "{{exePath}}"
        $logDir = "{{logDir}}"
        function Write-WatchdogLog($msg) {
            $ts = (Get-Date).ToString("HH:mm:ss.fff")
            $logFile = Get-ChildItem "$logDir\*-log.txt" -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1 -ExpandProperty FullName
            if (-not $logFile) { return }
            [System.IO.File]::AppendAllText($logFile, "$ts [Watchdog] $msg`r`n", [System.Text.Encoding]::UTF8)
        }
        try {
            $parent = [System.Diagnostics.Process]::GetProcessById($parentPid)
            Write-WatchdogLog "watching parent PID=$parentPid"
            $parent.WaitForExit()
            $exitCode = $parent.ExitCode
            Write-WatchdogLog "parent exited with code $exitCode"
            if ($exitCode -eq 0) { exit }
        } catch {
            Write-WatchdogLog "error: $_"
            exit
        }
        $rapidCrashCount = 0
        while ($true) {
            $start = Get-Date
            $child = Start-Process $exePath -ArgumentList '--watchdog', '--App:SuppressAlreadyRunningWarning=true' -PassThru
            $child.WaitForExit()
            $childExit = $child.ExitCode
            $elapsed = [Math]::Round(((Get-Date) - $start).TotalSeconds, 1)
            Write-WatchdogLog "child exited with code $childExit (ran ${elapsed}s)"
            if ($childExit -eq 0) { exit }
            if ($elapsed -ge 30) { $rapidCrashCount = 0; continue }
            $rapidCrashCount++
            if ($rapidCrashCount -ge 3) {
                Write-WatchdogLog "3 rapid crashes in a row, giving up"
                exit
            }
        }
        """;
        File.WriteAllText(scriptPath, script);

        _watchdogProcess = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-WindowStyle Hidden -NoProfile -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        _logger?.LogTrace("AutoRestart: watchdog started (PID={Pid})", _watchdogProcess?.Id);
    }

    public static void StopWatchdog()
    {
        if (_watchdogProcess is null) return;
        _logger?.LogTrace("AutoRestart: stopping watchdog (PID={Pid})", _watchdogProcess.Id);
        try
        {
            if (!_watchdogProcess.HasExited)
            {
                _watchdogProcess.Kill();
                _watchdogProcess.WaitForExit(3000);
            }
        }
        catch { }
        _watchdogProcess = null;
        // Clean up the temp script file
        if (_watchdogScriptPath is not null && File.Exists(_watchdogScriptPath))
            try { File.Delete(_watchdogScriptPath); } catch { }
        _watchdogScriptPath = null;
        _logger?.LogTrace("AutoRestart: watchdog stopped");
    }

    public static bool IsRunning => _watchdogProcess is { HasExited: false };
}
