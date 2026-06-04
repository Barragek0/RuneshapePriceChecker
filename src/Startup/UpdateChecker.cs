using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RuneshapePriceChecker.Startup;

internal sealed class UpdateChecker(
    IOptions<UpdateOptions> updateOptions,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateChecker> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = updateOptions.Value;
        if (!opts.AutoUpdate)
        {
            return;
        }

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
            latest = await FetchLatestReleaseAsync(opts.GitHubRepoOwner, opts.GitHubRepoName, opts.IgnorePrereleases);
        }
        catch (Exception ex)
        {
            logger.LogInformation("GitHub API unreachable ({Reason}). Assuming up to date.", ex.Message);
            return;
        }

        if (latest is null)
        {
            logger.LogInformation("No GitHub releases found. Assuming up to date.");
            return;
        }

        var latestVersionText = (latest.TagName ?? string.Empty).TrimStart('v', 'V');
        if (!TryParseVersion(latestVersionText, out var latestVersion))
        {
            logger.LogInformation("Cannot parse GitHub tag '{Tag}' as semver. Skipping update.", latest.TagName);
            return;
        }

        if (latestVersion <= currentVersion)
        {
            logger.LogInformation("Already up to date ({Current} >= {Latest}).", currentVersion, latestVersion);
            return;
        }

        var zipAsset = latest.Assets?.FirstOrDefault(a =>
            a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true &&
            a.BrowserDownloadUrl is not null);

        if (zipAsset is null)
        {
            logger.LogInformation("Latest release has no .zip asset. Skipping update.");
            return;
        }

        logger.LogInformation("Update available: {Current} -> {Latest}", currentVersion, latestVersion);

        var updaterPath = Path.Combine(installDir, "Update.exe");
        if (!File.Exists(updaterPath))
        {
            logger.LogWarning("Update.exe not found. Cannot apply update.");
            return;
        }

        logger.LogInformation("Launching updater for version {Version}...", latestVersion);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"--url \"{zipAsset.BrowserDownloadUrl}\" --version \"{latestVersionText}\"",
                WorkingDirectory = installDir,
                UseShellExecute = true
            });

            logger.LogInformation("Updater launched. Shutting down to allow update...");
            lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to launch Update.exe.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<GitHubRelease?> FetchLatestReleaseAsync(string owner, string repo, bool ignorePrereleases)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RuneshapePriceChecker", "1.0"));
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

            var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseProp) &&
                               prereleaseProp.ValueKind == JsonValueKind.True;

            if (ignorePrereleases && isPrerelease) continue;

            var tagName = release.TryGetProperty("tag_name", out var tagProp) &&
                          tagProp.ValueKind == JsonValueKind.String
                ? tagProp.GetString()!
                : string.Empty;

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

            return new GitHubRelease(tagName, assets);
        }

        return null;
    }

    private static bool TryParseVersion(string text, out Version version)
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
}

internal sealed class UpdateOptions
{
    public bool AutoUpdate { get; set; } = true;
    public bool IgnorePrereleases { get; set; } = false;
    public string GitHubRepoOwner { get; set; } = "Barragek0";
    public string GitHubRepoName { get; set; } = "RuneshapePriceChecker";
}

internal sealed record GitHubRelease(string TagName, List<GitHubAsset>? Assets);
internal sealed record GitHubAsset(string? Name, string? BrowserDownloadUrl, long? Size);
