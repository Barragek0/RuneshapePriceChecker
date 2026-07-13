using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Startup;

namespace RuneshapePriceChecker.App;

internal sealed class BugReportService(
    DashboardService dashboard,
    ILogger<BugReportService> logger,
    IOptionsMonitor<OcrOptions> ocrOptions)
{
    internal static bool IsTestMode
    {
        get
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "--App:TestMode=true", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    private const string GitHubRepo = "Barragek0/RuneshapePriceChecker";
    private static readonly string BackupFileName = $"bug-report-snapshot.{DateTime.Now:yyyyMMdd-HHmmss}.json";

    private string? _settingsBackupPath;
    private LogLevel _originalLogLevel = LogLevel.Information;

    public void StartBugReportFlow()
    {
        try
        {
            var configDir = Path.Combine(AppContext.BaseDirectory, "config");
            var configPath = Path.Combine(configDir, "appsettings.json");
            if (!File.Exists(configPath))
            {
                logger.LogError("Bug report: appsettings.json not found at {Path}", configPath);
                dashboard.LogError("Bug report: settings file not found — cannot proceed.");
                return;
            }

            _settingsBackupPath = Path.Combine(configDir, BackupFileName);
            File.Copy(configPath, _settingsBackupPath, overwrite: true);

            // Snapshot the current effective log level so we can restore it later.
            _originalLogLevel = CrashLogger.MinimumLogLevel;

            EnableDiagnosticMode(configPath);

            dashboard.SetOnBugReportContinue(OnBugReportContinue);
            dashboard.SetOnBugReportDone(FinishAndRestore);
            dashboard.SetOnBugReportCancel(FinishAndRestore);
            dashboard.ShowBugReportPrompt();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bug report: failed to start flow: {Context}", ErrorContext.FromException(ex));
            dashboard.LogError($"Bug report: {ex.Message}");
        }
    }

    private void OnBugReportContinue()
    {
        try
        {
            dashboard.SetStatus("Collecting bug report data…", "amber");

            var outputDir = PrepareOutputDirectory();
            var fileCount = CollectData(outputDir);

            // Zip into the parent directory first (to avoid locking conflicts
            // when the .zip is inside the source directory), then move it in.
            var stamp = new DirectoryInfo(outputDir).Name;
            var zipName = stamp + ".zip";
            var parentDir = Path.GetDirectoryName(outputDir)!;
            var tempZip = Path.Combine(parentDir, zipName);
            if (File.Exists(tempZip)) File.Delete(tempZip);
            ZipFile.CreateFromDirectory(outputDir, tempZip);

            // Delete source files individually — safer than recursive Directory.Delete
            // which can fail silently when files are locked, leaving stale artifacts.
            foreach (var f in Directory.GetFiles(outputDir))
                try { File.Delete(f); } catch { }

            File.Move(tempZip, Path.Combine(outputDir, zipName));

            if (!IsTestMode)
            {
                OpenUrl(BuildGitHubIssueUrl());
                OpenFolderInExplorer(outputDir);
            }

            dashboard.ShowBugReportDataCollected(fileCount, zipName);
            dashboard.SetStatus("Bug report data collected — GitHub issue opened", "green");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bug report: data collection failed: {Context}", ErrorContext.FromException(ex));
            dashboard.LogError($"Bug report: {ex.Message}");
            FinishAndRestore();
        }
    }

    private void FinishAndRestore()
    {
        RestoreSettings();
        dashboard.HideBugReportAll();
    }

    private void EnableDiagnosticMode(string configPath)
    {
        try
        {
            var json = File.ReadAllText(configPath, Encoding.UTF8);
            if (JsonNode.Parse(json) is not JsonObject root) return;

            root["App"] ??= new JsonObject();
            if (root["App"] is JsonObject app)
                app["LogLevel"] = "Trace";

            CrashLogger.MinimumLogLevel = LogLevel.Trace;

            root["OCR"] ??= new JsonObject();
            if (root["OCR"] is JsonObject ocr)
            {
                ocr["DebugOverlay"] = true;
                ocr["SaveDebugImages"] = true;
                ocr["DebugImageIntervalSeconds"] = 1;
            }

            if (root["App"] is JsonObject app2)
                app2["AlwaysOnTop"] = true;

            var newJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            File.WriteAllText(configPath, newJson, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bug report: failed to enable diagnostic mode: {Context}", ErrorContext.FromException(ex));
        }
    }

    private void RestoreSettings()
    {
        if (_settingsBackupPath is null || !File.Exists(_settingsBackupPath)) return;

        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
            File.Copy(_settingsBackupPath, configPath, overwrite: true);
            File.Delete(_settingsBackupPath);
            _settingsBackupPath = null;

            // Restore the runtime log level to its pre-bug-report value.
            CrashLogger.MinimumLogLevel = _originalLogLevel;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bug report: failed to restore settings: {Context}", ErrorContext.FromException(ex));
        }
    }

    private static string PrepareOutputDirectory()
    {
        var reportsDir = Path.Combine(AppContext.BaseDirectory, "logs", "bug-reports");
        _ = Directory.CreateDirectory(reportsDir);

        // Keep last 5 bug-reports
        foreach (var stale in Directory.GetDirectories(reportsDir)
            .OrderByDescending(d => d).Skip(5))
        {
            try { Directory.Delete(stale, recursive: true); } catch { }
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(reportsDir, $"bug-report-{stamp}");
        _ = Directory.CreateDirectory(dir);

        // If this exact path already existed (same-second collision), empty it first.
        foreach (var f in Directory.GetFiles(dir))
            try { File.Delete(f); } catch { }

        return dir;
    }

    private int CollectData(string outputDir)
    {
        var count = 0;
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");

        if (!Directory.Exists(logsDir)) logsDir = Directory.CreateDirectory(logsDir).FullName;

        // Search top-level logs/ only — never recurse into bug-reports/ subfolders.
        // Log files are named {timestamp}-log.txt (FileLogProvider), so use -log.txt
        // not .log.
        var latestLog = Directory.GetFiles(logsDir, "*-log.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(1)
            .FirstOrDefault();
        if (latestLog is not null)
        {
            try { File.Copy(latestLog, Path.Combine(outputDir, Path.GetFileName(latestLog))); count++; } catch { /* best effort */ }
        }

        // Most recent crash file
        var latestCrash = Directory.GetFiles(logsDir, "*-crash.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(1)
            .FirstOrDefault();
        if (latestCrash is not null)
        {
            try { File.Copy(latestCrash, Path.Combine(outputDir, Path.GetFileName(latestCrash))); count++; } catch { /* best effort */ }
        }

        // Most recent caught-exception file
        var latestCaught = Directory.GetFiles(logsDir, "*-caught.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(1)
            .FirstOrDefault();
        if (latestCaught is not null)
        {
            try { File.Copy(latestCaught, Path.Combine(outputDir, Path.GetFileName(latestCaught))); count++; } catch { /* best effort */ }
        }

        // Most recent native crash file (VEH — catches crashes managed handlers can't reach)
        var latestNative = Directory.GetFiles(logsDir, "*-native-crash.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(1)
            .FirstOrDefault();
        if (latestNative is not null)
        {
            try { File.Copy(latestNative, Path.Combine(outputDir, Path.GetFileName(latestNative))); count++; } catch { /* best effort */ }
        }

        // Debug images — only from the actively used OCR backend.
        // Config always stores "windows" or "tesseract" (no "auto" in the UI).
        var cfg = ocrOptions.CurrentValue.OcrBackend;
        var be = string.Equals(cfg, "tesseract", StringComparison.OrdinalIgnoreCase) ? "tesseract"
            : Environment.OSVersion.Version.Build < 17763 ? "tesseract"
            : "windows";
        var imagesDir = Path.Combine(AppContext.BaseDirectory, "ocr", be, "images");
        if (Directory.Exists(imagesDir))
        {
            foreach (var img in Directory.GetFiles(imagesDir, "*.png")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(10))
            {
                try { File.Copy(img, Path.Combine(outputDir, Path.GetFileName(img))); count++; } catch { /* best effort */ }
            }
        }

        // System info
        WriteSystemInfo(outputDir);
        count++;

        return count;
    }

    private void WriteSystemInfo(string outputDir)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== System Information ===");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"App Version: {GetAppVersion()}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"64-bit OS: {Environment.Is64BitOperatingSystem}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"64-bit Process: {Environment.Is64BitProcess}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Processors: {Environment.ProcessorCount}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
            sb.AppendLine(CultureInfo.InvariantCulture, $"CLR Version: {Environment.Version}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Current Directory: {Environment.CurrentDirectory}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Base Directory: {AppContext.BaseDirectory}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Command Line: {Environment.CommandLine}");
            sb.AppendLine();
            sb.AppendLine("=== Installed .NET Runtimes ===");
            sb.AppendLine(GetDotnetInfo());
            sb.AppendLine();
            sb.AppendLine("=== Settings Snapshot ===");
            if (_settingsBackupPath is not null && File.Exists(_settingsBackupPath))
                sb.AppendLine(File.ReadAllText(_settingsBackupPath, Encoding.UTF8));

            File.WriteAllText(Path.Combine(outputDir, "system-info.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bug report: failed to write system info.");
        }
    }

    private static string BuildGitHubIssueUrl()
    {
        var title = Uri.EscapeDataString("[BUG] Brief description of the issue");

        var body = new StringBuilder();
        body.AppendLine("**Describe the bug**");
        body.AppendLine("A clear and concise description of the bug");
        body.AppendLine();
        body.AppendLine();
        body.AppendLine("Drag and drop the .zip file the app gave you here");

        var bodyEncoded = Uri.EscapeDataString(body.ToString());
        return $"https://github.com/{GitHubRepo}/issues/new?title={title}&body={bodyEncoded}&assignees=Barragek0&labels=bug";
    }

    private static void OpenFolderInExplorer(string directory)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{directory}\"",
                    UseShellExecute = true
                }
            };
            _ = proc.Start();
        }
        catch (Exception ex)
        {
            // Non-critical — logging is enough
            try { System.Diagnostics.Debug.WriteLine($"Failed to open folder: {ex.Message}"); } catch { }
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                }
            };
            _ = proc.Start();
        }
        catch (Exception ex)
        {
            // Fallback: try cmd /c start
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd",
                        Arguments = $"/c start \"\" \"{url}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                _ = proc.Start();
            }
            catch
            {
                throw new InvalidOperationException($"Could not open browser. Please manually open:\n{url}", ex);
            }
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var asm = typeof(BugReportService).Assembly;
            var ver = asm.GetName().Version;
            return ver?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetDotnetInfo()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            _ = proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? "(not available)" : output;
        }
        catch
        {
            return "(not available)";
        }
    }
}
