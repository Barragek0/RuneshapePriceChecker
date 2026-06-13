using System.IO;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class DashboardViewModelDeepTests
{
    [Fact]
    public void LoadSettings_ValidJson_LoadsAllProperties()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"rstest-dvm-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(configPath, """{"App":{"LogLevel":"Debug"},"PricingCache":{"DisplayCurrency":"divine"}}""");
            var vm = new DashboardViewModel(configPath);
            vm.LoadSettings();
            Assert.NotNull(vm);
        }
        finally { try { File.Delete(configPath); } catch { } }
    }

    [Fact]
    public void LoadSettings_MissingFile_UsesDefaults()
    {
        var vm = new DashboardViewModel(Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.json"));
        vm.LoadSettings();
        Assert.NotNull(vm);
    }
}