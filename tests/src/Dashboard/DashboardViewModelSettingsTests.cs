using System.IO;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class DashboardViewModelSettingsTests
{
    private static string TempPath()
    {
        return Path.Combine(Path.GetTempPath(), $"rstest-settings-{Guid.NewGuid():N}.json");
    }

    [Fact]
    public void LogLevel_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.LogLevel = "Debug",
                  (before, after) => Assert.Equal(before.LogLevel, after.LogLevel));
    }

    [Fact]
    public void PricingSource_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.PricingSource = "poeninja",
                  (before, after) => Assert.Equal(before.PricingSource, after.PricingSource));
    }

    [Fact]
    public void League_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.CurrentLeague = "Test League 42",
                  (before, after) => Assert.Equal(before.CurrentLeague, after.CurrentLeague));
    }

    [Fact]
    public void DisplayCurrency_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.DisplayCurrency = "exalt",
                  (before, after) => Assert.Equal(before.DisplayCurrency, after.DisplayCurrency));
    }

    [Fact]
    public void Thresholds_RoundTrip_PreservesAllThree()
    {
        RoundTrip(vm =>
        {
            vm.RedThreshold = 0.8m;
            vm.OrangeThreshold = 1.5m;
            vm.GreenThreshold = 10m;
        }, (before, after) =>
        {
            Assert.Equal(before.RedThreshold, after.RedThreshold);
            Assert.Equal(before.OrangeThreshold, after.OrangeThreshold);
            Assert.Equal(before.GreenThreshold, after.GreenThreshold);
        });
    }

    [Fact]
    public void OcrLanguage_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.OcrLanguage = "fra",
                  (before, after) => Assert.Equal(before.OcrLanguage, after.OcrLanguage));
    }

    [Fact]
    public void DebugOverlay_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.DebugOverlay = true,
                  (before, after) => Assert.True(after.DebugOverlay));
    }

    [Fact]
    public void HideDebugOverlayWhenInterfaceNotDetected_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.HideDebugOverlayWhenInterfaceNotDetected = true,
                  (before, after) => Assert.True(after.HideDebugOverlayWhenInterfaceNotDetected));
    }

    [Fact]
    public void SaveDebugImages_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.SaveDebugImages = true,
                  (before, after) => Assert.True(after.SaveDebugImages));
    }

    [Fact]
    public void ShowPricingOverlay_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.ShowPricingOverlay = false,
                  (before, after) => Assert.False(after.ShowPricingOverlay));
    }

    [Fact]
    public void ShowBanner_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.ShowBanner = false,
                  (before, after) => Assert.False(after.ShowBanner));
    }

    [Fact]
    public void AutoUpdate_RoundTrip_PreservesValue()
    {
        RoundTrip(vm => vm.AutoUpdate = false,
                  (before, after) => Assert.False(after.AutoUpdate));
    }

    [Fact]
    public void AllSettings_DefaultValues_AreCorrect()
    {
        var path = TempPath();
        try
        {
            var vm = new DashboardViewModel(path);
            vm.LoadSettings();

            Assert.Equal("Information", vm.LogLevel);
            Assert.Equal("poe2scout", vm.PricingSource);
            Assert.Equal("Runes of Aldur", vm.CurrentLeague);
            Assert.Equal("exalt", vm.DisplayCurrency);
            Assert.Equal(0.5m, vm.RedThreshold);
            Assert.Equal(1.0m, vm.OrangeThreshold);
            Assert.Equal(5.0m, vm.GreenThreshold);
            Assert.Equal("eng", vm.OcrLanguage);
            Assert.False(vm.DebugOverlay);
            Assert.False(vm.HideDebugOverlayWhenInterfaceNotDetected);
            Assert.False(vm.SaveDebugImages);
            Assert.True(vm.ShowPricingOverlay);
            Assert.True(vm.ShowBanner);
            Assert.True(vm.AutoUpdate);
        }
        finally { TryDelete(path); }
    }

    private static void RoundTrip(Action<DashboardViewModel> mutate, Action<DashboardViewModel, DashboardViewModel> assert)
    {
        var path = TempPath();
        try
        {
            var vm = new DashboardViewModel(path);
            vm.LoadSettings();
            mutate(vm);
            _ = vm.SaveSettings();

            var vm2 = new DashboardViewModel(path);
            vm2.LoadSettings();
            assert(vm, vm2);
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
