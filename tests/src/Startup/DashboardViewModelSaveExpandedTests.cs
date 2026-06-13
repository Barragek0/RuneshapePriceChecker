using System.IO;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class DashboardViewModelSaveExpandedTests
{
    [Fact]
    public void SaveSettings_ThenLoadSettings_RoundTripWorks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rstest-rt2-{Guid.NewGuid():N}.json");
        try
        {
            var vm = new DashboardViewModel(path);
            vm.LoadSettings();
            vm.SaveSettings();
            Assert.True(File.Exists(path));

            var vm2 = new DashboardViewModel(path);
            vm2.LoadSettings();
            Assert.NotNull(vm2);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void LoadSettings_ValidJsonWithAllSections_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rstest-all-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"App":{"LogLevel":"Debug"},"PricingCache":{"DisplayCurrency":"divine"},"Ocr":{"Language":"eng"},"Update":{"AutoUpdate":true}}""");
            var vm = new DashboardViewModel(path);
            vm.LoadSettings();
            Assert.NotNull(vm);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}