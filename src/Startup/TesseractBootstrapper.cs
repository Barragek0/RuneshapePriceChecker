using System.Diagnostics;

namespace RuneshapePriceChecker.Startup;

internal static class TesseractBootstrapper
{
    private const string Category = "Bootstrap.Tesseract[0]";

    private static readonly string[] CommonInstallPaths =
    [
        @"C:\Program Files\Tesseract-OCR\tesseract.exe",
        @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
        @"C:\Program Files\Tesseract\tesseract.exe"
    ];

    public static string EnsureInstalled(string configuredPath)
    {
        if (TryResolveTesseractPath(configuredPath, out var resolvedPath))
        {
            LogInfo($"Tesseract available at '{resolvedPath}'; OCR is enabled.");
            return resolvedPath;
        }

        if (!OperatingSystem.IsWindows())
        {
            LogWarning("Tesseract not found and auto-install is only supported on Windows. OCR may be unavailable.");
            return configuredPath;
        }

        LogWarning($"Tesseract not found at configured path '{configuredPath}'.");
        LogInfo("Installing Tesseract via winget...");

        if (!TryRunWingetInstall())
        {
            LogWarning("Automatic install failed. Install Tesseract manually.");
            return configuredPath;
        }

        if (TryResolveTesseractPath(configuredPath, out resolvedPath))
        {
            LogInfo($"Tesseract install completed. Resolved executable path: '{resolvedPath}'.");
            return resolvedPath;
        }

        LogWarning("Tesseract installation completed but executable path could not be resolved automatically.");
        return configuredPath;
    }

    private static bool TryRunWingetInstall()
    {
        if (!TryEnsureWingetAvailable())
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = "install --id UB-Mannheim.TesseractOCR -e --accept-package-agreements --accept-source-agreements --disable-interactivity",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        return RunProcessWithLiveLogging(startInfo, "winget install tesseract", TimeSpan.FromMinutes(5));
    }

    private static bool TryEnsureWingetAvailable()
    {
        if (IsExecutableAvailable("winget", out var wingetPath))
        {
            LogInfo($"winget available at '{wingetPath}'.");
            return true;
        }

        LogWarning("winget was not found on PATH; cannot auto-install Tesseract.");
        return false;
    }

    private static bool RunProcessWithLiveLogging(ProcessStartInfo startInfo, string operationName, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                LogWarning($"Failed to start process for {operationName}.");
                return false;
            }

            var stdoutCount = 0;
            var stderrCount = 0;
            var stdoutSuppressed = 0;
            var stderrSuppressed = 0;
            const int maxLoggedLinesPerStream = 60;
            var installAlreadyPresent = false;

            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                {
                    return;
                }

                var line = args.Data.Trim();
                if (ShouldIgnoreProgressNoise(line))
                {
                    return;
                }

                if (IndicatesAlreadyInstalledOrUpToDate(line))
                {
                    installAlreadyPresent = true;
                }

                if (stdoutCount < maxLoggedLinesPerStream)
                {
                    stdoutCount++;
                    LogInfo($"{operationName}: {line}");
                }
                else
                {
                    stdoutSuppressed++;
                }
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data))
                {
                    return;
                }

                var line = args.Data.Trim();
                if (ShouldIgnoreProgressNoise(line))
                {
                    return;
                }

                if (IndicatesAlreadyInstalledOrUpToDate(line))
                {
                    installAlreadyPresent = true;
                }

                if (stderrCount < maxLoggedLinesPerStream)
                {
                    stderrCount++;
                    LogWarning($"{operationName}: {line}");
                }
                else
                {
                    stderrSuppressed++;
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var startedAt = DateTime.UtcNow;
            var nextHeartbeat = startedAt.AddSeconds(12);

            while (!process.HasExited)
            {
                if (DateTime.UtcNow - startedAt > timeout)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best effort timeout cleanup.
                    }

                    LogWarning($"{operationName} timed out after {(int)timeout.TotalMinutes} minutes.");
                    return false;
                }

                if (DateTime.UtcNow >= nextHeartbeat)
                {
                    var elapsed = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
                    LogInfo($"{operationName} is still running ({elapsed}s elapsed)...");
                    nextHeartbeat = DateTime.UtcNow.AddSeconds(12);
                }

                process.WaitForExit(500);
            }

            process.WaitForExit();

            if (stdoutSuppressed > 0)
            {
                LogInfo($"{operationName}: suppressed {stdoutSuppressed} additional stdout lines.");
            }

            if (stderrSuppressed > 0)
            {
                LogWarning($"{operationName}: suppressed {stderrSuppressed} additional stderr lines.");
            }

            if (process.ExitCode != 0)
            {
                if (operationName == "winget install tesseract" && installAlreadyPresent)
                {
                    LogInfo("winget reported Tesseract is already installed and up to date.");
                    return true;
                }

                LogWarning($"{operationName} failed with exit code {process.ExitCode}.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogWarning($"{operationName} threw an exception: {ex.Message}");
            return false;
        }
    }

    private static void LogInfo(string message)
    {
        Console.WriteLine($"info: {Category} {message}");
    }

    private static void LogWarning(string message)
    {
        Console.WriteLine($"warn: {Category} {message}");
    }

    private static bool IsExecutableAvailable(string executable, out string resolvedPath)
    {
        resolvedPath = executable;

        if (Path.IsPathRooted(executable))
        {
            if (!File.Exists(executable))
            {
                return false;
            }

            resolvedPath = executable;
            return true;
        }

        var candidates = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { executable }
            : new[] { executable, $"{executable}.exe" };

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathSegments = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in pathSegments)
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(segment.Trim(), candidate);
                if (File.Exists(fullPath))
                {
                    resolvedPath = fullPath;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryResolveTesseractPath(string configuredPath, out string resolvedPath)
    {
        if (IsExecutableAvailable(configuredPath, out resolvedPath))
        {
            return true;
        }

        if (IsExecutableAvailable("tesseract", out resolvedPath))
        {
            return true;
        }

        foreach (var candidate in CommonInstallPaths)
        {
            if (File.Exists(candidate))
            {
                resolvedPath = candidate;
                return true;
            }
        }

        resolvedPath = configuredPath;
        return false;
    }

    private static bool ShouldIgnoreProgressNoise(string line)
    {
        return line is "-" or "\\" or "|" or "/";
    }

    private static bool IndicatesAlreadyInstalledOrUpToDate(string line)
    {
        return line.Contains("Found an existing package already installed", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("No available upgrade found", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("No newer package versions are available", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("already installed", StringComparison.OrdinalIgnoreCase);
    }
}
