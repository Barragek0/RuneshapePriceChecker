using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class UpdateCheckerFlowTests
{
    private static readonly Type? UpdateCheckerType = Type.GetType("RuneshapePriceChecker.Startup.UpdateChecker, RuneshapePriceChecker");

    [Fact]
    public void TryParseVersion_ValidSemver_ReturnsTrue()
    {
        var method = GetTryParseVersion();
        var args = new object?[] { "1.2.3", null! };
        var result = (bool)method.Invoke(null, args)!;
        Assert.True(result);
        Assert.Equal("1.2.3", ((Version)args[1]!).ToString());
    }

    [Fact]
    public void TryParseVersion_PrefixedWithV_ReturnsFalse()
    {
        var method = GetTryParseVersion();
        var args = new object?[] { "v1.0.0", null! };
        var result = (bool)method.Invoke(null, args)!;
        Assert.False(result);
    }

    [Fact]
    public void TryParseVersion_InvalidFormat_ReturnsFalse()
    {
        var method = GetTryParseVersion();
        var args = new object?[] { "not-a-version", null! };
        var result = (bool)method.Invoke(null, args)!;
        Assert.False(result);
    }

    [Fact]
    public void TryParseVersion_SingleDigit_ReturnsFalse()
    {
        var method = GetTryParseVersion();
        var args = new object?[] { "1", null! };
        var result = (bool)method.Invoke(null, args)!;
        Assert.False(result);
    }

    [Fact]
    public void TryParseVersion_TwoPart_ReturnsFalse()
    {
        var method = GetTryParseVersion();
        var args = new object?[] { "1.2", null! };
        var result = (bool)method.Invoke(null, args)!;
        Assert.False(result);
    }

    [Fact]
    public void TryParseVersion_FourPart_ReturnsFalse()
    {
        var method = GetTryParseVersion();
        var args = new object?[] { "1.2.3.4", null! };
        var result = (bool)method.Invoke(null, args)!;
        Assert.False(result);
    }

    [Fact]
    public void TryParseVersion_EmptyString_ReturnsFalse()
    {
        var method = GetTryParseVersion();
        var args = new object?[] { "", null! };
        var result = (bool)method.Invoke(null, args)!;
        Assert.False(result);
    }

    [Fact]
    public void ChangelogFullCycle_WriteReadMarkShown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rstest-update-{Guid.NewGuid():N}.json");
        try
        {
            WriteChangelogJson(path, "1.0.0");

            var vm = new DashboardViewModel(path);

            var pending = vm.TryGetPendingChangelogVersion();
            Assert.NotNull(pending);
            Assert.Equal("1.0.0", pending);

            vm.MarkChangelogShown();
            Assert.Null(vm.TryGetPendingChangelogVersion());
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ChangelogJson_PreservesExistingSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rstest-update-{Guid.NewGuid():N}.json");
        try
        {
            var root = new JsonObject
            {
                ["App"] = new JsonObject { ["LogLevel"] = "Trace" },
                ["Pricing"] = new JsonObject { ["League"] = "Test League" },
                ["Changelog"] = new JsonObject
                {
                    ["Version"] = "2.0.0",
                    ["Shown"] = false
                }
            };
            var dir = Path.GetDirectoryName(path)!;
            _ = Directory.CreateDirectory(dir);
            File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, Encoding.UTF8);

            var vm = new DashboardViewModel(path);
            vm.LoadSettings();

            Assert.Equal("Trace", vm.LogLevel);
            Assert.Equal("Test League", vm.CurrentLeague);

            var pending = vm.TryGetPendingChangelogVersion();
            Assert.NotNull(pending);
            Assert.Equal("2.0.0", pending);

            vm.MarkChangelogShown();

            var vm2 = new DashboardViewModel(path);
            vm2.LoadSettings();
            Assert.Equal("Trace", vm2.LogLevel);
            Assert.Null(vm2.TryGetPendingChangelogVersion());
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static MethodInfo GetTryParseVersion()
    {
        Assert.NotNull(UpdateCheckerType);
        var method = UpdateCheckerType!.GetMethod("TryParseVersion", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }

    private static void WriteChangelogJson(string path, string version)
    {
        var root = new JsonObject
        {
            ["Changelog"] = new JsonObject
            {
                ["Version"] = version,
                ["Shown"] = false
            }
        };
        var dir = Path.GetDirectoryName(path)!;
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, Encoding.UTF8);
    }
}
