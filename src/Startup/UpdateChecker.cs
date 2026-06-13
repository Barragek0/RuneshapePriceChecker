using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;

namespace RuneshapePriceChecker.Startup;

internal sealed class UpdateChecker(
    IOptions<UpdateOptions> updateOptions,
    IOptionsMonitor<AppOptions> appOptions,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateChecker> logger,
    DashboardService dashboard,
    IHttpClientFactory httpClientFactory) : IHostedService
{
    private string? _downloadUrl;
    private string? _localZipPath;
    private string? _latestVersion;
    private string? _changelogBody;
    private string? _changelogVersion;
    private CancellationToken _stoppingToken;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;
        DashboardService.UpdateTrigger = progress => _ = ApplyUpdateAsync(progress);

        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckForUpdatesAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    private async Task CheckForUpdatesAsync()
    {
        var opts = updateOptions.Value;
        var forceUpdate = appOptions.CurrentValue.ForceUpdateAvailable;

        if (!opts.AutoUpdate && !forceUpdate)
            return;

        dashboard.SetStatus("Checking for updates...", "green");
        logger.LogInformation("Checking for updates via GitHub...");

        var installDir = AppContext.BaseDirectory;
        var attr = typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var currentVersionText = attr?.InformationalVersion ?? "";
        if (string.IsNullOrWhiteSpace(currentVersionText))
        {
            logger.LogInformation("No embedded version found. Skipping update check.");
            return;
        }

        var plusIndex = currentVersionText.IndexOf('+');
        if (plusIndex >= 0) currentVersionText = currentVersionText[..plusIndex];

        if (!TryParseVersion(currentVersionText, out var currentVersion))
        {
            logger.LogInformation("Cannot parse current version '{Version}'. Skipping update check.", currentVersionText);
            return;
        }

        logger.LogInformation("Current version: {Version}", currentVersion);

        GitHubRelease? latest;
        try
        {
            latest = await FetchLatestReleaseWithRetryAsync(opts.GitHubRepoOwner, opts.GitHubRepoName, opts.IgnorePrereleases);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (latest is null)
        {
            logger.LogInformation("No GitHub releases found. Assuming up to date.");
            if (forceUpdate)
            {
                _localZipPath = FindLocalReleaseZip();
                if (_localZipPath is not null)
                {
                    _downloadUrl = "local";
                    dashboard.ShowUpdateButton();
                }
            }
            return;
        }

        var zipAsset = latest.Assets?.FirstOrDefault(a =>
            a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true &&
            a.BrowserDownloadUrl is not null);

        if (zipAsset is null && !forceUpdate)
        {
            logger.LogInformation("Latest release has no .zip asset. Skipping update.");
            return;
        }

        var latestVersionText = (latest.TagName ?? string.Empty).TrimStart('v', 'V');
        if (!TryParseVersion(latestVersionText, out var latestVersion))
        {
            logger.LogInformation("Cannot parse GitHub tag '{Tag}' as semver. Skipping update.", latest.TagName);
            return;
        }

        if (latestVersion <= currentVersion && !forceUpdate)
        {
            logger.LogInformation("Already up to date ({Current} >= {Latest}).", currentVersion, latestVersion);
            dashboard.SetStatus("Up to date", "green");

            if (latestVersion == currentVersion && zipAsset is not null)
            {
                await RepairUpdaterIfNeededAsync(zipAsset, currentVersionText, installDir);
            }

            return;
        }

        if (forceUpdate)
        {
            _localZipPath = FindLocalReleaseZip();
            if (_localZipPath is not null)
            {
                _downloadUrl = "local";
                logger.LogInformation("ForceUpdateAvailable: using local zip {Path}", _localZipPath);
            }
            else if (zipAsset?.BrowserDownloadUrl is not null)
            {
                _downloadUrl = zipAsset.BrowserDownloadUrl;
            }
        }
        else if (zipAsset?.BrowserDownloadUrl is not null)
        {
            _downloadUrl = zipAsset.BrowserDownloadUrl;
        }

        _latestVersion = latestVersionText;
        _changelogBody = latest.Body;
        _changelogVersion = latestVersionText;
        dashboard.ShowUpdateButton();

        if (latestVersion > currentVersion)
            logger.LogInformation("Update available: {Current} -> {Latest}", currentVersion, latestVersion);
    }

    private static string? FindLocalReleaseZip()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "bin", "Release", "RuneshapePriceChecker.zip"),
            Path.Combine(AppContext.BaseDirectory, "RuneshapePriceChecker.zip"),
        };

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full)) return full;
        }

        return null;
    }

    public async Task ApplyUpdateAsync(IProgress<int>? progress = null)
    {
        if (_downloadUrl is null)
        {
            logger.LogWarning("No download URL available. Cannot apply update.");
            return;
        }

        WriteChangelogToSettings();

        var installDir = AppContext.BaseDirectory;
        var tempZip = Path.Combine(Path.GetTempPath(), $"runeshape-update-{Guid.NewGuid():N}.zip");

        try
        {
            logger.LogInformation("Starting update download...");
            progress?.Report(0);

            if (_downloadUrl == "local" && _localZipPath is not null)
            {
                File.Copy(_localZipPath, tempZip);
                logger.LogInformation("Copied local zip for update simulation.");
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                using var response = await http.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1;
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = File.Create(tempZip);

                var buffer = new byte[8192];
                var downloaded = 0L;
                var lastReported = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    int pct;
                    if (total > 0)
                        pct = (int)(downloaded * 100 / total);
                    else
                        pct = Math.Min(99, lastReported + 1);

                    if (pct != lastReported && progress is not null)
                    {
                        lastReported = pct;
                        progress.Report(pct);
                    }
                }
            }

            logger.LogInformation("Download complete. Extracting updater...");
            progress?.Report(100);

            if (_downloadUrl == "local")
            {
                progress?.Report(100);

                var scriptPath = Path.Combine(Path.GetTempPath(), $"runeshape-update-{Guid.NewGuid():N}.ps1");
                File.WriteAllText(scriptPath,
                    $"$zip = '{tempZip}'\r\n" +
                    $"$dest = '{installDir}'\r\n" +
                    $"$staging = Join-Path $env:TEMP \"runeshape-staging-$(New-Guid)\"\r\n" +
                    $"Expand-Archive -Path $zip -DestinationPath $staging -Force\r\n" +
                    $"Remove-Item $zip -Force\r\n" +
                    $"Get-ChildItem $staging -Recurse | Copy-Item -Destination $dest -Force -ErrorAction SilentlyContinue\r\n" +
                    $"Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
                    $"Start-Process (Join-Path $dest 'RuneshapePriceChecker.exe')\r\n" +
                    $"Remove-Item '{scriptPath}' -Force -ErrorAction SilentlyContinue\r\n");

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                lifetime.StopApplication();
                return;
            }
            else
            {
                var updaterPath = Path.Combine(installDir, "Update.exe");
                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    var updaterEntry = archive.Entries.FirstOrDefault(e =>
                        e.Name.Equals("Update.exe", StringComparison.OrdinalIgnoreCase));
                    if (updaterEntry is not null)
                    {
                        var tempUpdater = updaterPath + ".new";
                        updaterEntry.ExtractToFile(tempUpdater, overwrite: true);
                        try { File.Delete(updaterPath); } catch { }
                        File.Move(tempUpdater, updaterPath);
                    }
                }

                File.Delete(tempZip);

                logger.LogInformation("Launching updater...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = $"--url \"{_downloadUrl}\" --version \"{_latestVersion ?? "0.0.0"}\"",
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }

            await Task.Delay(500);
            lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            try { File.Delete(tempZip); } catch { }
            logger.LogError(ex, "Update failed.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RepairUpdaterIfNeededAsync(
        GitHubAsset? zipAsset, string expectedVersionText, string installDir)
    {
        const string updaterExeName = "Update.exe";
        var updaterPath = Path.Combine(installDir, updaterExeName);

        if (!File.Exists(updaterPath))
        {
            logger.LogInformation("Update.exe not present; nothing to repair.");
            return;
        }

        Version? expectedVersion = null;
        _ = TryParseVersion(expectedVersionText, out expectedVersion);

        Version? diskVersion = null;
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(updaterPath);
            if (fvi.FileVersion is not null)
            {
                var clean = fvi.FileVersion;
                var plusIdx = clean.IndexOf('+');
                if (plusIdx >= 0) clean = clean[..plusIdx];
                if (!TryParseVersion(clean, out diskVersion))
                {
                    var lastDot = clean.LastIndexOf('.');
                    if (lastDot >= 0) _ = TryParseVersion(clean[..lastDot], out diskVersion);
                }
            }
        }
        catch
        {
            logger.LogWarning("Could not read Update.exe file version.");
        }

        if (diskVersion is not null && expectedVersion is not null && diskVersion >= expectedVersion)
        {
            return;
        }

        logger.LogWarning(
            "Update.exe is outdated (disk={DiskVersion}, expected={ExpectedVersion}). Repairing...",
            diskVersion,
            expectedVersion);

        if (zipAsset?.BrowserDownloadUrl is null)
        {
            logger.LogWarning("No release zip URL available. Cannot repair Update.exe.");
            return;
        }

        logger.LogInformation("Downloading current release zip to extract Update.exe...");

        var tempZip = Path.Combine(Path.GetTempPath(), $"runeshape-selfrepair-{Guid.NewGuid():N}.zip");
        try
        {
            using var http = httpClientFactory.CreateClient("GitHub");
            await using var stream = await http.GetStreamAsync(zipAsset.BrowserDownloadUrl);
            await using var fileStream = File.Create(tempZip);
            await stream.CopyToAsync(fileStream);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download zip for Update.exe repair.");
            try { File.Delete(tempZip); } catch { }
            return;
        }

        try
        {
            using var archive = ZipFile.OpenRead(tempZip);
            var updaterEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals(updaterExeName, StringComparison.OrdinalIgnoreCase));

            if (updaterEntry is null)
            {
                logger.LogWarning("Update.exe not found in release zip.");
                return;
            }

            var tempExtracted = updaterPath + ".repairtmp";
            updaterEntry.ExtractToFile(tempExtracted, overwrite: true);

            try { File.Delete(updaterPath); } catch { }
            File.Move(tempExtracted, updaterPath);

            logger.LogInformation("Update.exe repaired successfully.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract Update.exe from zip.");
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    private async Task<GitHubRelease?> FetchLatestReleaseWithRetryAsync(string owner, string repo, bool ignorePrereleases)
    {
        while (!_stoppingToken.IsCancellationRequested)
        {
            try
            {
                return await FetchLatestReleaseAsync(owner, repo, ignorePrereleases).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError("GitHub API unreachable ({Reason}). Retrying in 10s...", ex.Message);
                dashboard.SetStatus("GitHub API unreachable — retrying...", "red");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }

        _stoppingToken.ThrowIfCancellationRequested();
        return null;
    }

    private async Task<GitHubRelease?> FetchLatestReleaseAsync(string owner, string repo, bool ignorePrereleases)
    {
        using var http = httpClientFactory.CreateClient("GitHub");

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

            var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseProp) &&
                               prereleaseProp.ValueKind == JsonValueKind.True;

            if (ignorePrereleases && isPrerelease) continue;

            var tagName = release.TryGetProperty("tag_name", out var tagProp) &&
                          tagProp.ValueKind == JsonValueKind.String
                ? tagProp.GetString()!
                : string.Empty;

            var body = release.TryGetProperty("body", out var bodyProp) &&
                       bodyProp.ValueKind == JsonValueKind.String
                ? bodyProp.GetString()
                : null;

            var assets = new List<GitHubAsset>();
            if (release.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    var downloadUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? dlProp.GetString() : null;
                    var size = asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var s) ? s : (long?)null;
                    assets.Add(new GitHubAsset(name, downloadUrl, size));
                }
            }

            return new GitHubRelease(tagName, assets, body);
        }

        return null;
    }

    private void WriteChangelogToSettings()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
            var configDir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);

            System.Text.Json.Nodes.JsonNode root;
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath, System.Text.Encoding.UTF8);
                root = System.Text.Json.Nodes.JsonNode.Parse(json) ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }

            if (root["Changelog"] is not System.Text.Json.Nodes.JsonObject changelog)
            {
                changelog = [];
                root["Changelog"] = changelog;
            }

            changelog["Shown"] = false;
            if (!string.IsNullOrWhiteSpace(_changelogBody))
                changelog["Body"] = _changelogBody;
            if (!string.IsNullOrWhiteSpace(_changelogVersion))
                changelog["Version"] = _changelogVersion;

            var jsonResult = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, jsonResult + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write changelog to settings.");
        }
    }

    internal static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0);
        var match = Regex.Match(text, @"^(\d+)\.(\d+)\.(\d+)$");
        if (!match.Success) return false;

        version = new Version(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        return true;
    }
}

internal sealed class UpdateOptions
{
    public bool AutoUpdate { get; set; } = true;
    public bool IgnorePrereleases { get; set; }
    public string GitHubApiBaseUrl { get; set; } = "https://api.github.com";
    public string GitHubRepoOwner { get; set; } = "Barragek0";
    public string GitHubRepoName { get; set; } = "RuneshapePriceChecker";
}

internal sealed record GitHubRelease(string TagName, List<GitHubAsset>? Assets, string? Body = null);
internal sealed record GitHubAsset(string? Name, string? BrowserDownloadUrl, long? Size);
