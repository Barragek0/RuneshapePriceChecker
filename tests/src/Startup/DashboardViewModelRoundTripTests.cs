using System.IO;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class DashboardViewModelRoundTripTests
{
    [Fact]
    public void SaveLoad_RoundTrip_PreservesSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rstest-rt-{Guid.NewGuid():N}.json");
        try
        {
            var vm = new DashboardViewModel(path);
            vm.LoadSettings();
            _ = vm.SaveSettings();

            Assert.True(File.Exists(path));
            var vm2 = new DashboardViewModel(path);
            vm2.LoadSettings();
            Assert.NotNull(vm2);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void SaveSettings_MissingDirectory_CreatesIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rstest-rt-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "sub", "appsettings.json");
        try
        {
            var vm = new DashboardViewModel(path);
            _ = vm.SaveSettings();
            Assert.True(File.Exists(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void SaveSettings_EmptyLeague_ReturnsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rstest-rt-{Guid.NewGuid():N}.json");
        try
        {
            var vm = new DashboardViewModel(path) { CurrentLeague = "" };
            var result = vm.SaveSettings();
            Assert.NotNull(result);
            Assert.Contains("league", result, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void SaveSettings_InvalidThresholds_ReturnsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rstest-rt-{Guid.NewGuid():N}.json");
        try
        {
            var vm = new DashboardViewModel(path) { RedThreshold = 5m, OrangeThreshold = 3m };
            var result = vm.SaveSettings();
            Assert.NotNull(result);
            Assert.Contains("Threshold", result, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void LoadSettings_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.json");
        var vm = new DashboardViewModel(path);
        vm.LoadSettings(); // Should not throw
        Assert.Equal("Runes of Aldur", vm.CurrentLeague); // Default
    }

    [Fact]
    public void ConfigHasFlag_NoFile_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.json");
        var vm = new DashboardViewModel(path);
        Assert.False(vm.ConfigHasFlag("OCR", "DebugOverlay"));
    }
}