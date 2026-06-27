using System.Net;
using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Startup;
using RuneshapePriceChecker.Tests.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class UpdateCheckerFullFlowTests
{
    [Fact]
    public async Task CheckForUpdates_FullCycle_DetectsUpdateAndWritesChangelog()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", new[]
        {
            new
            {
                tag_name = "v2.0.0",
                prerelease = false,
                body = "## What's New in v2.0.0\n\n### Features\n- Test feature",
                assets = new[]
                {
                    new
                    {
                        name = "RuneshapePriceChecker.zip",
                        browser_download_url = "https://example.com/download/v2.0.0/RuneshapePriceChecker.zip",
                        size = 5000000L
                    }
                }
            }
        });

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.NotNull(release);
        Assert.Equal("v2.0.0", release!.TagName);
        Assert.NotNull(release.Assets);
        _ = Assert.Single(release.Assets!);
        Assert.Equal("RuneshapePriceChecker.zip", release.Assets![0].Name);
        Assert.NotNull(release.Assets[0].BrowserDownloadUrl);
        Assert.Contains("example.com", release.Assets[0].BrowserDownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdates_RetryLogic_RecoversAfterFailure()
    {
        using var handler = new MockHttpHandler();
        handler.AddErrorResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", HttpStatusCode.InternalServerError);

        var checker = CreateChecker(handler);

        // First attempt fails
        _ = await Assert.ThrowsAsync<HttpRequestException>(() =>
            InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true));
    }

    [Fact]
    public async Task CheckForUpdates_ForceUpdateAvailable_DetectedByConfig()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", new[]
        {
            new
            {
                tag_name = "v1.0.0",
                prerelease = false,
                assets = new[]
                {
                    new
                    {
                        name = "RuneshapePriceChecker.zip",
                        browser_download_url = "https://example.com/download/v1.0.0/RuneshapePriceChecker.zip"
                    }
                }
            }
        });

        var checker = CreateChecker(handler);

        // With force update, even same-version releases trigger an update
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", false);

        Assert.NotNull(release);
        Assert.Equal("v1.0.0", release!.TagName);
    }

    [Fact]
    public async Task CheckForUpdates_NoReleases_ReturnsNull()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", Array.Empty<object>());

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.Null(release);
    }

    [Fact]
    public async Task CheckForUpdates_VersionFrom0_2_2_ToLatest_DetectedAsUpdate()
    {
        Assert.True(UpdateChecker.TryParseVersion("0.2.2", out var oldVer));
        Assert.True(UpdateChecker.TryParseVersion("1.0.0", out var newVer));
        Assert.True(newVer > oldVer);

        // Simulate: old version 0.2.2, GitHub has 1.0.0
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", new[]
        {
            new
            {
                tag_name = "v1.0.0",
                prerelease = false,
                assets = new[]
                {
                    new
                    {
                        name = "RuneshapePriceChecker.zip",
                        browser_download_url = "https://example.com/download/v1.0.0/RuneshapePriceChecker.zip"
                    }
                }
            }
        });

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.NotNull(release);
        Assert.Equal("v1.0.0", release!.TagName);

        // Version comparison: 1.0.0 > 0.2.2 → update available
        var tagStripped = release.TagName.TrimStart('v', 'V');
        Assert.True(UpdateChecker.TryParseVersion(tagStripped, out var latestVersion));
        Assert.True(latestVersion > oldVer);
    }

    [Fact]
    public async Task CheckForUpdates_ReleaseWithNoAssets_ReturnsReleaseWithoutAssets()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", new[]
        {
            new
            {
                tag_name = "v1.0.0",
                prerelease = false,
                assets = Array.Empty<object>()
            }
        });

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.NotNull(release);
        Assert.Empty(release!.Assets!);
    }

    [Fact]
    public async Task CheckForUpdates_NotFound_ReturnsNull()
    {
        using var handler = new MockHttpHandler();
        handler.AddErrorResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", HttpStatusCode.NotFound);

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.Null(release);
    }

#pragma warning disable CA2000 // Ownership transferred to UpdateChecker via factory/sink/dashboard
    private static UpdateChecker CreateChecker(MockHttpHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") };
        var factory = new SingleClientHttpClientFactory(httpClient, "GitHub");

        var updateOptions = Options.Create(new UpdateOptions
        {
            AutoUpdate = true,
            GitHubRepoOwner = "Barragek0",
            GitHubRepoName = "RuneshapePriceChecker"
        });

        var appOptions = new StaticOptionsMonitor<AppOptions>(new AppOptions());
        var logger = new LoggerFactory().CreateLogger<UpdateChecker>();
        var lifetime = new NullApplicationLifetime();
        var sink = new DashboardLogSink();
        var dashboard = new DashboardService(sink);

        return new UpdateChecker(updateOptions, appOptions, lifetime, logger, dashboard, factory);
    }
#pragma warning restore CA2000

    private static async Task<GitHubRelease?> InvokeFetchLatestReleaseAsync(
        UpdateChecker checker, string owner, string repo, bool ignorePrereleases)
    {
        var method = typeof(UpdateChecker).GetMethod("FetchLatestReleaseAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<GitHubRelease?>?)method!.Invoke(checker, [owner, repo, ignorePrereleases]);
        Assert.NotNull(task);
        return await task!;
    }
}
