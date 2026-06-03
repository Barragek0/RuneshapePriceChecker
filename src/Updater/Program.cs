using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

if (!AttachConsole())
{
    AllocConsole();
}

Console.Title = "RuneshapePriceChecker Updater";
Console.WriteLine();
Console.WriteLine("======================================================");
Console.WriteLine("       RuneshapePriceChecker Auto-Updater");
Console.WriteLine("======================================================");
Console.WriteLine();

var args_ = ParseArgs(args);
if (string.IsNullOrWhiteSpace(args_.DownloadUrl))
{
    Fail("Missing --url argument with the zip download URL.");
}

if (string.IsNullOrWhiteSpace(args_.NewVersion))
{
    Fail("Missing --version argument with the new version string.");
}

Log($"Update to version {args_.NewVersion}");
Log($"Download URL: {args_.DownloadUrl}");

var installDir = AppContext.BaseDirectory;
Log($"Install directory: {installDir}");

var tempZip = Path.Combine(Path.GetTempPath(), $"runeshape-update-{Guid.NewGuid():N}.zip");

Log("Downloading update...");
using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
{
    try
    {
        await using var stream = await http.GetStreamAsync(args_.DownloadUrl);
        await using var fileStream = File.Create(tempZip);
        await stream.CopyToAsync(fileStream);
    }
    catch (Exception ex)
    {
        Fail($"Download failed: {ex.Message}");
    }
}
Log($"Downloaded: {FormatBytes(new FileInfo(tempZip).Length)}");

var mainExeName = "RuneshapePriceChecker.exe";
await CloseMainProcessAsync(mainExeName);

Log("Extracting update...");
await Task.Run(() => ExtractZip(tempZip, installDir));

try { File.Delete(tempZip); } catch { }

Log($"Update complete: {args_.NewVersion}");

var mainExePath = Path.Combine(installDir, mainExeName);
if (!File.Exists(mainExePath))
{
    Log($"WARNING: {mainExeName} not found. Manual restart required.");
}
else
{
    Log($"Starting: {mainExePath}");
    Process.Start(new ProcessStartInfo
    {
        FileName = mainExePath,
        WorkingDirectory = installDir,
        UseShellExecute = true
    });
}

Log("Done.");

static void Log(string message)
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {message}");
}

static void Fail(string message)
{
    Log($"FATAL: {message}");
    Console.WriteLine();
    Console.WriteLine("Press any key to close...");
    Console.ReadKey(intercept: true);
    Environment.Exit(1);
}

static string FormatBytes(long bytes)
{
    if (bytes < 1024) return $"{bytes} B";
    if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
    return $"{bytes / (1024.0 * 1024.0):F1} MB";
}

static UpdaterArgs ParseArgs(string[] args)
{
    string? url = null;
    string? version = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--url" when i + 1 < args.Length:
                url = args[++i];
                break;
            case "--version" when i + 1 < args.Length:
                version = args[++i];
                break;
        }
    }

    return new UpdaterArgs(url, version);
}

static async Task CloseMainProcessAsync(string exeName)
{
    var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName));
    if (processes.Length == 0)
    {
        Log("Main app is not running; proceeding.");
        return;
    }

    Log($"Closing main app ({processes.Length} process(es))...");

    foreach (var proc in processes)
    {
        try { if (!proc.HasExited) proc.CloseMainWindow(); } catch { }
    }

    var waited = 0;
    const int maxWaitMs = 5000;
    while (waited < maxWaitMs)
    {
        await Task.Delay(250);
        waited += 250;
        if (!processes.Any(p => { try { return !p.HasExited; } catch { return false; } }))
        {
            Log("Main app closed gracefully.");
            return;
        }
    }

    Log("Force-killing main app...");
    foreach (var proc in processes)
    {
        try { if (!proc.HasExited) { proc.Kill(); proc.WaitForExit(3000); } } catch { }
    }
    Log("Main app terminated.");
}

static void ExtractZip(string zipPath, string destinationDir)
{
    using var archive = ZipFile.OpenRead(zipPath);
    foreach (var entry in archive.Entries)
    {
        if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
        if (entry.Name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            var destConfig = Path.Combine(destinationDir, entry.FullName);
            if (File.Exists(destConfig)) continue;
        }

        var destPath = Path.Combine(destinationDir, entry.FullName);
        var destDir = Path.GetDirectoryName(destPath)!;
        if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

        for (var retry = 0; retry < 5; retry++)
        {
            try { entry.ExtractToFile(destPath, overwrite: true); break; }
            catch (IOException) when (retry < 4) { Thread.Sleep(500); }
        }
    }
}

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool AllocConsole();

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool AttachConsole(uint dwProcessId = uint.MaxValue);

record UpdaterArgs(string? DownloadUrl, string? NewVersion);
