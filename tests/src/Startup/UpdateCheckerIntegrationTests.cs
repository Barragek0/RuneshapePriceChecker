using System.Net;
using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Startup;
using RuneshapePriceChecker.Tests.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class UpdateCheckerIntegrationTests
{
    [Fact]
    public async Task FetchLatestRelease_ValidReleaseWithZip_ParsesCorrectly()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", new[]
        {
            new
            {
                tag_name = "v1.2.3",
                prerelease = false,
                html_url = "https://github.com/Barragek0/RuneshapePriceChecker/releases/tag/v1.2.3",
                body = "## What's New\n\n- Feature A\n- Feature B",
                assets = new[]
                {
                    new
                    {
                        name = "RuneshapePriceChecker.zip",
                        browser_download_url = "https://github.com/Barragek0/RuneshapePriceChecker/releases/download/v1.2.3/RuneshapePriceChecker.zip",
                        size = 12345678L
                    }
                }
            }
        });

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.NotNull(release);
        Assert.Equal("v1.2.3", release!.TagName);
        Assert.NotNull(release.Assets);
        _ = Assert.Single(release.Assets);
        Assert.Equal("RuneshapePriceChecker.zip", release.Assets![0].Name);
        Assert.NotNull(release.Assets[0].BrowserDownloadUrl);
    }

    [Fact]
    public async Task FetchLatestRelease_MultipleReleases_SkipsPrereleases()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", new[]
        {
            new
            {
                tag_name = "v2.0.0-beta",
                prerelease = true,
                assets = new[]
                {
                    new { name = "RuneshapePriceChecker.zip", browser_download_url = "https://example.com/beta.zip" }
                }
            },
            new
            {
                tag_name = "v1.0.0",
                prerelease = false,
                assets = new[]
                {
                    new { name = "RuneshapePriceChecker.zip", browser_download_url = "https://example.com/stable.zip" }
                }
            }
        });

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.NotNull(release);
        Assert.Equal("v1.0.0", release!.TagName);
    }

    [Fact]
    public async Task FetchLatestRelease_IgnoresPrereleasesFalse_ReturnsPrerelease()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", new[]
        {
            new
            {
                tag_name = "v2.0.0-beta",
                prerelease = true,
                assets = new[]
                {
                    new { name = "RuneshapePriceChecker.zip", browser_download_url = "https://example.com/beta.zip" }
                }
            }
        });

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", false);

        Assert.NotNull(release);
        Assert.Equal("v2.0.0-beta", release!.TagName);
    }

    [Fact]
    public async Task FetchLatestRelease_NoZipAsset_StillParsesRelease()
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
        Assert.Equal("v1.0.0", release!.TagName);
        Assert.NotNull(release.Assets);
        Assert.Empty(release.Assets!);
    }

    [Fact]
    public async Task FetchLatestRelease_EmptyArray_ReturnsNull()
    {
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", Array.Empty<object>());

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.Null(release);
    }

    [Fact]
    public async Task FetchLatestRelease_NotFound_ReturnsNull()
    {
        using var handler = new MockHttpHandler();
        handler.AddErrorResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", HttpStatusCode.NotFound);

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.Null(release);
    }

    [Fact]
    public async Task FetchLatestRelease_ServerError_Throws()
    {
        using var handler = new MockHttpHandler();
        handler.AddErrorResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", HttpStatusCode.InternalServerError);

        var checker = CreateChecker(handler);
        _ = await Assert.ThrowsAsync<HttpRequestException>(() =>
            InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true));
    }

    [Fact]
    public async Task FetchLatestRelease_ReleaseWithBody_ParsesReleaseTag()
    {
        var expectedBody = "## Changelog\n\n### Features\n- Item 1\n- Item 2";
        using var handler = new MockHttpHandler();
        handler.AddResponse("/repos/Barragek0/RuneshapePriceChecker/releases?per_page=10", new[]
        {
            new
            {
                tag_name = "v1.0.0",
                prerelease = false,
                body = expectedBody,
                html_url = "https://github.com/release/1",
                assets = new[]
                {
                    new { name = "RuneshapePriceChecker.zip", browser_download_url = "https://example.com/zip" }
                }
            }
        });

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.NotNull(release);
        Assert.Equal("v1.0.0", release!.TagName);
        Assert.NotNull(release.Assets);
        _ = Assert.Single(release.Assets!);
    }

    [Fact]
    public async Task FetchLatestRelease_AssetWithoutSize_HandlesNull()
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
                    new { name = "RuneshapePriceChecker.zip", browser_download_url = "https://example.com/zip" }
                }
            }
        });

        var checker = CreateChecker(handler);
        var release = await InvokeFetchLatestReleaseAsync(checker, "Barragek0", "RuneshapePriceChecker", true);

        Assert.NotNull(release);
        Assert.NotNull(release!.Assets);
        _ = Assert.Single(release.Assets!);
        Assert.Null(release.Assets![0].Size);
    }

#pragma warning disable CA2000 // Ownership transferred to UpdateChecker via factory/sink/dashboard
    private static UpdateChecker CreateChecker(MockHttpHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com")
        };
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

internal sealed class SingleClientHttpClientFactory(HttpClient client, string name) : IHttpClientFactory
{
    public HttpClient CreateClient(string requestedName)
    {
        if (requestedName == name) return client;
        return new HttpClient();
    }
}

internal sealed class NullApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}