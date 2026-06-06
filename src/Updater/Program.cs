using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

if (!Console.IsOutputRedirected && !AttachConsole())
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
var installDir = AppContext.BaseDirectory;

if (!string.IsNullOrWhiteSpace(args_.DownloadUrl))
{
    Log("Launched by main app (direct URL mode).");
    await RunDirectUpdateAsync(args_.DownloadUrl, args_.NewVersion ?? "unknown", installDir);
}
else
{
    Log("Standalone mode — checking GitHub for updates...");
    await RunStandaloneCheckAsync(args_.RepoOwner, args_.RepoName, installDir);
    Log("Done.");
    Console.WriteLine();
    Console.WriteLine("Press any key to close...");
    Console.ReadKey(intercept: true);
}

static async Task RunDirectUpdateAsync(string downloadUrl, string newVersion, string installDir)
{
    Log($"Update to version {newVersion}");
    Log($"Download URL: {downloadUrl}");

    var tempZip = Path.Combine(Path.GetTempPath(), $"runeshape-update-{Guid.NewGuid():N}.zip");

    Log("Downloading update...");
    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
    {
        try
        {
            await using var stream = await http.GetStreamAsync(downloadUrl);
            await using var fileStream = File.Create(tempZip);
            await stream.CopyToAsync(fileStream);
        }
        catch (Exception ex)
        {
            Fail($"Download failed: {ex.Message}");
        }
    }
    Log($"Downloaded: {FormatBytes(new FileInfo(tempZip).Length)}");

    await FinishUpdateAsync(tempZip, newVersion, installDir);
}

static async Task RunStandaloneCheckAsync(string owner, string repo, string installDir)
{
    var attr = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
    var currentVersionText = attr?.InformationalVersion ?? "";
    if (string.IsNullOrWhiteSpace(currentVersionText))
    {
        Fail("Cannot determine current version from embedded assembly info.");
    }

    var plusIndex = currentVersionText.IndexOf('+');
    if (plusIndex >= 0) currentVersionText = currentVersionText[..plusIndex];

    if (!TryParseVersion(currentVersionText, out var currentVersion))
    {
        Fail($"Cannot parse embedded version '{currentVersionText}'.");
    }

    Log($"Current version: {currentVersion}");
    Log($"Repo: {owner}/{repo}");

    GitHubRelease? latest = null;
    try
    {
        latest = await FetchLatestReleaseAsync(owner, repo);
    }
    catch (Exception ex)
    {
        Log($"GitHub API unreachable ({ex.Message}).");
        Fail("Cannot check for updates — GitHub is unreachable.");
    }

    if (latest is null)
    {
        Log("No GitHub releases found. Already up to date.");
        return;
    }

    var latestVersionText = (latest.TagName ?? "").TrimStart('v', 'V');
    if (!TryParseVersion(latestVersionText, out var latestVersion))
    {
        Log($"Cannot parse GitHub tag '{latest.TagName}' as semver.");
        return;
    }

    if (latestVersion <= currentVersion)
    {
        Log($"Already up to date ({currentVersion} >= {latestVersion}).");
        return;
    }

    var zipAsset = latest.Assets?.FirstOrDefault(a =>
        a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true &&
        a.BrowserDownloadUrl is not null);

    if (zipAsset is null)
    {
        Log("Latest release has no .zip asset.");
        return;
    }

    Log($"Update available: {currentVersion} -> {latestVersion}");
    await RunDirectUpdateAsync(zipAsset.BrowserDownloadUrl!, latestVersionText, installDir);
}

static async Task FinishUpdateAsync(string tempZip, string newVersion, string installDir)
{
    const string mainExeName = "RuneshapePriceChecker.exe";
    const string updaterExeName = "Update.exe";
    await CloseMainProcessAsync(mainExeName);

    var oldUpdaterPath = Path.Combine(installDir, $"{updaterExeName}.old");
    try { if (File.Exists(oldUpdaterPath)) File.Delete(oldUpdaterPath); } catch { }
    var currentUpdaterPath = Path.Combine(installDir, updaterExeName);
    try { if (File.Exists(currentUpdaterPath)) File.Move(currentUpdaterPath, oldUpdaterPath); } catch { }

    Log("Extracting update...");
    await Task.Run(() => ExtractZip(tempZip, installDir));

    try { File.Delete(tempZip); } catch { }

    Log($"Update complete: {newVersion}");

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
}

static async Task<GitHubRelease?> FetchLatestReleaseAsync(string owner, string repo)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RuneshapePriceChecker-Updater", "1.0"));
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

    var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=10";

    var response = await http.GetAsync(url);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return null;
    }

    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadFromJsonAsync<JsonElement>();
    if (json.ValueKind != JsonValueKind.Array) return null;

    foreach (var release in json.EnumerateArray())
    {
        if (release.ValueKind != JsonValueKind.Object) continue;

        var tagName = release.TryGetProperty("tag_name", out var tagProp) &&
                      tagProp.ValueKind == JsonValueKind.String
            ? tagProp.GetString()!
            : "";

        var assets = new List<GitHubAsset>();
        if (release.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsProp.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                var downloadUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() : null;
                assets.Add(new GitHubAsset(name, downloadUrl));
            }
        }

        return new GitHubRelease(tagName, assets);
    }

    return null;
}

static bool TryParseVersion(string text, out Version version)
{
    version = new Version(0, 0);
    var match = Regex.Match(text, @"^(\d+)\.(\d+)\.(\d+)$");
    if (!match.Success) return false;

    version = new Version(
        int.Parse(match.Groups[1].Value),
        int.Parse(match.Groups[2].Value),
        int.Parse(match.Groups[3].Value));
    return true;
}

static void Log(string message)
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {message}");
}

[DoesNotReturn]
static void Fail(string message)
{
    Log($"FATAL: {message}");
    Console.WriteLine();
    Log("The console will close in 10 seconds...");
    Thread.Sleep(10000);
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
    var owner = "Barragek0";
    var repo = "RuneshapePriceChecker";

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
            case "--owner" when i + 1 < args.Length:
                owner = args[++i];
                break;
            case "--repo" when i + 1 < args.Length:
                repo = args[++i];
                break;
        }
    }

    return new UpdaterArgs(url, version, owner, repo);
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
    var selfExe = Path.GetFileName(Environment.ProcessPath);
    var failed = new List<string>();
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
        if (selfExe is not null &&
            entry.Name.Equals(selfExe, StringComparison.OrdinalIgnoreCase))
        {
            destPath += ".new";
        }

        var destDir = Path.GetDirectoryName(destPath)!;
        if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

        var extracted = false;
        for (var retry = 0; retry < 5; retry++)
        {
            try
            {
                if (File.Exists(destPath))
                {
                    try { File.Delete(destPath); } catch { }
                }
                entry.ExtractToFile(destPath, overwrite: true);
                extracted = true;
                break;
            }
            catch (IOException)
            {
                if (retry < 4) { Thread.Sleep(500); }
            }
        }

        if (!extracted)
            failed.Add(entry.FullName);
    }

    if (failed.Count > 0)
    {
        Log($"WARNING: {failed.Count} file(s) could not be extracted: {string.Join(", ", failed)}");
    }
}

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool AllocConsole();

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool AttachConsole(uint dwProcessId = uint.MaxValue);

record UpdaterArgs(string? DownloadUrl, string? NewVersion, string RepoOwner, string RepoName);
record GitHubRelease(string TagName, List<GitHubAsset>? Assets);
record GitHubAsset(string? Name, string? BrowserDownloadUrl);
