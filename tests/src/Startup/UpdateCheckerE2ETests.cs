using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class UpdateCheckerE2ETests
{
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