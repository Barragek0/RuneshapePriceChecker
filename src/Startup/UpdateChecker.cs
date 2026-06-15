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

        // Repair broken v1.0.0 self-contained updater on startup
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            await RepairBrokenUpdaterIfNeededAsync();
        }, cancellationToken);

        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckForUpdatesAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    private async Task RepairBrokenUpdaterIfNeededAsync()
    {
        var installDir = AppContext.BaseDirectory;
        var updaterPath = Path.Combine(installDir, "Update.exe");
        if (!File.Exists(updaterPath)) return;
        if (new FileInfo(updaterPath).Length < 10_000_000) return;

        logger.LogWarning("Found broken v1.0.0 self-contained updater. Repairing...");
        try
        {
            var opts = updateOptions.Value;
            var latest = await FetchLatestReleaseWithRetryAsync(
                opts.GitHubRepoOwner, opts.GitHubRepoName, opts.IgnorePrereleases);
            if (latest is null) return;
            var zipAsset = latest.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true &&
                a.BrowserDownloadUrl is not null);
            if (zipAsset is null) return;

            var tempZip = Path.Combine(Path.GetTempPath(), $"runeshape-repair-{Guid.NewGuid():N}.zip");
            try
            {
                using var http = httpClientFactory.CreateClient("GitHub");
                await using var stream = await http.GetStreamAsync(zipAsset.BrowserDownloadUrl);
                await using var fs = File.Create(tempZip);
                await stream.CopyToAsync(fs);

                using var archive = ZipFile.OpenRead(tempZip);
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals("Update.exe", StringComparison.OrdinalIgnoreCase));
                if (entry is null) return;

                var tmp = updaterPath + ".repairtmp";
                entry.ExtractToFile(tmp, overwrite: true);
                try { File.Delete(updaterPath); } catch { }
                File.Move(tmp, updaterPath);
                logger.LogInformation("Update.exe repaired successfully.");
            }
            finally { try { File.Delete(tempZip); } catch { } }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Updater repair failed."); }
    }

    private void RunPowerShellUpdate(string zipPath, string installDir)
    {
        var escapedZip = zipPath.Replace("'", "''");
        var escapedInstall = installDir.Replace("'", "''");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"runeshape-update-{Guid.NewGuid():N}.ps1");
        var script = $@"
        $ErrorActionPreference = 'Stop'
        Start-Sleep -Seconds 2
        $staging = Join-Path $env:TEMP ""runeshape-staging-$(New-Guid)""
        try {{
            Expand-Archive -Path '{escapedZip}' -DestinationPath $staging -Force
            Remove-Item '{escapedZip}' -Force -ErrorAction SilentlyContinue
            Get-ChildItem $staging -Recurse -File | ForEach-Object {{
                $relative = $_.FullName.Substring($staging.Length + 1)
                $target = Join-Path '{escapedInstall}' $relative
                $targetDir = Split-Path $target -Parent
                if (-not (Test-Path $targetDir)) {{ New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }}
                Copy-Item $_.FullName $target -Force
            }}
            Remove-Item $staging -Recurse -Force
            Start-Process (Join-Path '{escapedInstall}' 'RuneshapePriceChecker.exe')
        }} catch {{
            Write-Error $_.Exception.Message
        }} finally {{
            Remove-Item '{scriptPath.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue
        }}
        ";
        File.WriteAllText(scriptPath, script);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to launch PowerShell update script.");
            try { File.Delete(scriptPath); } catch { }
            throw;
        }
        logger.LogInformation("PowerShell update script launched.");
    }

    private async Task CheckForUpdatesAsync()
    {
        var opts = updateOptions.Value;
        var forceUpdate = appOptions.CurrentValue.ForceUpdateAvailable;

        if (!opts.AutoUpdate && !forceUpdate)
            return;

        dashboard.SetStatus("Checking for updates...", "green");
        logger.LogInformation("Checking for updates...");

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

            if (latestVersion == currentVersion && zipAsset is not null)
            {
                await RepairUpdaterIfNeededAsync(zipAsset, currentVersionText, installDir);
            }

            if (latestVersion == currentVersion && !string.IsNullOrWhiteSpace(latest.Body))
            {
                _changelogBody = latest.Body;
                _changelogVersion = latestVersionText;
                WriteChangelogIfNotAlreadyShown();
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
            dashboard.HideUpdateOverlay();
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
            }

            logger.LogInformation("Extracting update...");
            RunPowerShellUpdate(tempZip, installDir);
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
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _stoppingToken.ThrowIfCancellationRequested();

            try
            {
                return await FetchLatestReleaseAsync(owner, repo, ignorePrereleases).ConfigureAwait(false);
            }
            catch (RateLimitExceededException rle)
            {
                logger.LogWarning(
                    "Github rate limit exceeded. Resets at {ResetTime:yyyy-MM-dd HH:mm:ss} UTC ({Remaining} remaining until then).",
                    rle.ResetTime, rle.RemainingString);
                dashboard.SetStatus("Github rate limited — waiting...", "orange");

                var waitTime = rle.ResetTime - DateTimeOffset.UtcNow;
                if (waitTime > TimeSpan.Zero && waitTime < TimeSpan.FromHours(1))
                {
                    try
                    {
                        await Task.Delay(waitTime, _stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
                else
                {
                    // Don't wait more than an hour; let the next scheduled check handle it
                    break;
                }
            }
            catch (Exception ex)
            {
                var delay = attempt switch
                {
                    0 => TimeSpan.FromSeconds(10),
                    1 => TimeSpan.FromSeconds(30),
                    _ => TimeSpan.FromSeconds(90)
                };

                logger.LogError(
                    "GitHub API unreachable ({Reason}). Retrying in {Delay}s... (attempt {Attempt}/3)",
                    ex.Message, (int)delay.TotalSeconds, attempt + 1);
                dashboard.SetStatus("GitHub API unreachable — retrying...", "red");

                try
                {
                    await Task.Delay(delay, _stoppingToken).ConfigureAwait(false);
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

        var baseUrl = updateOptions.Value.GitHubApiBaseUrl;
        var url = $"{baseUrl}/repos/{owner}/{repo}/releases?per_page=10";

        var response = await http.GetAsync(url);
        LogRateLimitHeaders(response);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or (System.Net.HttpStatusCode)429)
        {
            var resetTime = ReadRateLimitReset(response);
            var remaining = ReadRateLimitRemaining(response);
            throw new RateLimitExceededException(resetTime, remaining);
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

    private void WriteChangelogIfNotAlreadyShown()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
            if (!File.Exists(configPath))
            {
                WriteChangelogToSettings();
                return;
            }

            var json = File.ReadAllText(configPath, System.Text.Encoding.UTF8);
            var root = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (root?["Changelog"] is System.Text.Json.Nodes.JsonObject existing)
            {
                var shown = existing["Shown"]?.GetValue<bool>() ?? false;
                var existingVersion = existing["Version"]?.GetValue<string>();

                if (shown && string.Equals(existingVersion, _changelogVersion, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogTrace("Changelog for {Version} already shown, skipping.", _changelogVersion);
                    return;
                }
            }

            WriteChangelogToSettings();
            logger.LogInformation("Wrote pending changelog for {Version} to settings.", _changelogVersion);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check/write changelog to settings.");
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

    private void LogRateLimitHeaders(HttpResponseMessage response)
    {
        var remaining = ReadRateLimitRemaining(response);
        var reset = ReadRateLimitReset(response);

        if (remaining >= 0)
        {
            var resetStr = reset > DateTimeOffset.MinValue
                ? $", resets at {reset:yyyy-MM-dd HH:mm:ss} UTC"
                : string.Empty;
            if (remaining <= 5)
                logger.LogWarning("Github rate limit: {Remaining} remaining{ResetInfo}", remaining, resetStr);
            else if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Github rate limit: {Remaining} remaining{ResetInfo}", remaining, resetStr);
        }
    }

    private static int ReadRateLimitRemaining(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var remaining))
        {
            return remaining;
        }
        return -1;
    }

    private static DateTimeOffset ReadRateLimitReset(HttpResponseMessage response)
    {
        // Primary: X-RateLimit-Reset (Unix epoch seconds)
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
            long.TryParse(resetValues.FirstOrDefault(), out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        // Secondary: Retry-After header (seconds or HTTP-date)
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is not null)
        {
            if (retryAfter.Date.HasValue)
                return retryAfter.Date.Value;
            if (retryAfter.Delta.HasValue)
                return DateTimeOffset.UtcNow + retryAfter.Delta.Value;
        }

        return DateTimeOffset.MinValue;
    }
}

internal sealed class RateLimitExceededException(DateTimeOffset resetTime, int remaining)
    : Exception($"Github API rate limit exceeded. Resets at {resetTime:yyyy-MM-dd HH:mm:ss} UTC.")
{
    public DateTimeOffset ResetTime { get; } = resetTime;
    public string RemainingString { get; } = remaining >= 0 ? remaining.ToString(CultureInfo.InvariantCulture) : "?";
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
