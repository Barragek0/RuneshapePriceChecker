using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class ChangelogFlowTests
{
    [Fact]
    public void WriteChangelog_ThenTryGetPending_ReturnsVersion()
    {
        var path = GetTempConfigPath();
        try
        {
            WriteChangelogToFile(path, "1.0.0");

            var vm = new DashboardViewModel(path);
            var pending = vm.TryGetPendingChangelogVersion();

            Assert.NotNull(pending);
            Assert.Equal("1.0.0", pending);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void WriteChangelog_MarkShown_ThenTryGetPending_ReturnsNull()
    {
        var path = GetTempConfigPath();
        try
        {
            WriteChangelogToFile(path, "1.0.0");

            var vm = new DashboardViewModel(path);
            var pending = vm.TryGetPendingChangelogVersion();
            Assert.NotNull(pending);

            vm.MarkChangelogShown();

            var pending2 = vm.TryGetPendingChangelogVersion();
            Assert.Null(pending2);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void TryGetPendingChangelog_NoChangelogSection_ReturnsNull()
    {
        var path = GetTempConfigPath();
        try
        {
            File.WriteAllText(path, """{"App":{"LogLevel":"Information"}}""" + Environment.NewLine, Encoding.UTF8);

            var vm = new DashboardViewModel(path);
            var pending = vm.TryGetPendingChangelogVersion();

            Assert.Null(pending);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void TryGetPendingChangelog_EmptyVersion_ReturnsNull()
    {
        var path = GetTempConfigPath();
        try
        {
            WriteChangelogToFile(path, "");

            var vm = new DashboardViewModel(path);
            var pending = vm.TryGetPendingChangelogVersion();

            Assert.Null(pending);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void TryGetPendingChangelog_ShownTrue_ReturnsNull()
    {
        var path = GetTempConfigPath();
        try
        {
            WriteChangelogToFile(path, "1.0.0", shown: true);

            var vm = new DashboardViewModel(path);
            var pending = vm.TryGetPendingChangelogVersion();

            Assert.Null(pending);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void MarkChangelogShown_PreservesOtherSettings()
    {
        var path = GetTempConfigPath();
        try
        {
            var json = """
            {
                "App": {"LogLevel": "Debug"},
                "Changelog": {"Version": "1.0", "Shown": false}
            }
            """;
            File.WriteAllText(path, json + Environment.NewLine, Encoding.UTF8);

            var vm = new DashboardViewModel(path);
            vm.MarkChangelogShown();

            var vm2 = new DashboardViewModel(path);
            vm2.LoadSettings();
            Assert.Equal("Debug", vm2.LogLevel);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void TryGetPendingChangelog_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.json");
        var vm = new DashboardViewModel(path);
        Assert.Null(vm.TryGetPendingChangelogVersion());
    }

    private static string GetTempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"rstest-changelog-{Guid.NewGuid():N}.json");

    private static void WriteChangelogToFile(string path, string version, bool shown = false)
    {
        var root = new JsonObject
        {
            ["Changelog"] = new JsonObject
            {
                ["Version"] = version,
                ["Shown"] = shown
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, Encoding.UTF8);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
