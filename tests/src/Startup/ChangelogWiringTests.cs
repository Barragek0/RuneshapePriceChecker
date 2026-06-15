using Xunit;
using System.IO;
using System.Reflection;

namespace RuneshapePriceChecker.Tests.Startup;

public class ChangelogWiringTests
{
    [Fact]
    public void GitHubRelease_HasBodyField()
    {
        var type = Type.GetType("RuneshapePriceChecker.Startup.GitHubRelease, RuneshapePriceChecker");
        Assert.NotNull(type);
        var bodyProp = type!.GetProperty("Body");
        Assert.NotNull(bodyProp);
        Assert.Equal(typeof(string), bodyProp!.PropertyType);
    }

    [Fact]
    public void UpdateOptions_HasGitHubApiBaseUrl()
    {
        var type = Type.GetType("RuneshapePriceChecker.Startup.UpdateOptions, RuneshapePriceChecker");
        Assert.NotNull(type);
        var prop = type!.GetProperty("GitHubApiBaseUrl");
        Assert.NotNull(prop);
    }

    [Fact]
    public void UpdateChecker_HasWriteChangelogMethod()
    {
        var type = Type.GetType("RuneshapePriceChecker.Startup.UpdateChecker, RuneshapePriceChecker");
        Assert.NotNull(type);
        var method = type!.GetMethod("WriteChangelogToSettings", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void DashboardWindow_HasChangelogCheck()
    {
        var file = Path.Combine(RepoRoot, "src", "Dashboard", "DashboardWindow.xaml.cs");
        Assert.True(File.Exists(file), $"File not found: {file}");
        var content = File.ReadAllText(file);
        Assert.Contains("TryGetPendingChangelogVersion", content);
    }

    [Fact]
    public void DashboardViewModel_HasMarkChangelogShown()
    {
        var file = Path.Combine(RepoRoot, "src", "Dashboard", "DashboardViewModel.cs");
        Assert.True(File.Exists(file), $"File not found: {file}");
        var content = File.ReadAllText(file);
        Assert.Contains("MarkChangelogShown", content);
    }

    [Fact]
    public void ShowChangelogPreview_Exists()
    {
        var file = Path.Combine(RepoRoot, "src", "Dashboard", "DashboardWindow.xaml.cs");
        var content = File.ReadAllText(file);
        Assert.Contains("ShowChangelogPreview", content);
        Assert.Contains("--ShowChangelog", content);
    }
}
