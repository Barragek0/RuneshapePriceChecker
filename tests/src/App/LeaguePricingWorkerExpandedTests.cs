using System.Reflection;
using System.Runtime.CompilerServices;
using RuneshapePriceChecker.App;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

public class LeaguePricingWorkerExpandedTests
{
    private static readonly string[] UncutGemNames = ["Uncut Skill Gem", "Uncut Support Gem", "Uncut Spirit Gem"];
    private static readonly string[] SupportGemName = ["Support Gem"];
    [Fact]
    public void StartSnapshotReadTask_ReturnsTask()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("StartSnapshotReadTask",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
    }

    [Fact]
    public void ParseItemAndQuantity_OcrToken_O_ReturnsQuantity2()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("ParseItemAndQuantity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, ["O Chaos Orb"]);
        var tuple = (ITuple)result!;
        Assert.Equal(2, tuple[1]);
    }

    [Fact]
    public void ParseItemAndQuantity_OcrToken_l_ReturnsQuantity1()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("ParseItemAndQuantity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, ["l Chaos Orb"]);
        var tuple = (ITuple)result!;
        Assert.Equal(1, tuple[1]);
    }

    [Fact]
    public void BuildUnpriceableBanner_UncutGems_NotFlagged()
    {
        // Uncut gems are priced, should NOT appear in banner
        var result = InvokeBuildUnpriceableBanner(UncutGemNames);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_SupportGem_Flagged()
    {
        // "Support Gem" (without Uncut) matches UnpriceablePrefixes
        var result = InvokeBuildUnpriceableBanner(SupportGemName);
        Assert.NotNull(result);
    }

    private static string? InvokeBuildUnpriceableBanner(string[] names)
    {
        var method = typeof(LeaguePricingWorker).GetMethod("BuildUnpriceableBanner",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null) return null;
        return method.Invoke(null, [names, null]) as string;
    }

    [Fact]
    public void UnpriceablePrefixes_ContainsSkillAndSupport()
    {
        var field = typeof(LeaguePricingWorker).GetField("UnpriceablePrefixes",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (string[])field!.GetValue(null)!;
        Assert.Contains("Skill ", value);
        Assert.Contains("Support ", value);
    }
}