using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class UpdateCheckerE2ETests
{
    [Fact]
    public void UpdateOptions_ApiBaseUrl_Overrideable()
    {
        var opts = new UpdateOptions { GitHubApiBaseUrl = "http://localhost:8099/api" };
        Assert.Equal("http://localhost:8099/api", opts.GitHubApiBaseUrl);
    }

    [Fact]
    public void GitHubRelease_HasAssetsProperty()
    {
        var type = typeof(GitHubRelease);
        var prop = type.GetProperty("Assets");
        Assert.NotNull(prop);
    }

    [Fact]
    public void GitHubAsset_HasDownloadUrlProperty()
    {
        var type = typeof(GitHubAsset);
        var prop = type.GetProperty("BrowserDownloadUrl");
        Assert.NotNull(prop);
    }
}