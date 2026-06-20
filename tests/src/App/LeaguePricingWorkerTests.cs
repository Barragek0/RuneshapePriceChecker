using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using RuneshapePriceChecker.App;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

public class LeaguePricingWorkerTests
{
    private static readonly string[] SkillGemName = ["Skill Gem"];
    [Fact]
    public void LeaguePricingWorker_IsBackgroundService()
    {
        var type = typeof(LeaguePricingWorker);
        Assert.True(typeof(BackgroundService).IsAssignableFrom(type));
    }

    [Fact]
    public void ParseItemAndQuantity_StandardFormat_ReturnsCorrectValues()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("ParseItemAndQuantity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, ["1x Chaos Orb"]);
        var tuple = (ITuple)result!;
        Assert.Equal("Chaos Orb", tuple[0]);
        Assert.Equal(1, tuple[1]);
    }

    [Fact]
    public void ParseItemAndQuantity_NoQuantity_ReturnsOne()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("ParseItemAndQuantity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, ["Divine Orb"]);
        var tuple = (ITuple)result!;
        Assert.Equal(1, tuple[1]);
    }

    [Fact]
    public void IsRareUniqueItem_ExactMatch_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Rare Unique Item"])!);
    }

    [Fact]
    public void IsRareUniqueItem_CaseInsensitive_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["rare unique item"])!);
        Assert.True((bool)method!.Invoke(null, ["RARE UNIQUE ITEM"])!);
    }

    [Fact]
    public void IsRareUniqueItem_VeryRare_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Very Rare Unique Item"])!);
        Assert.True((bool)method!.Invoke(null, ["Very Rare Unique item"])!);
        Assert.True((bool)method!.Invoke(null, ["VERY RARE UNIQUE ITEM"])!);
    }

    [Fact]
    public void IsRareUniqueItem_OtherItem_ReturnsFalse()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method!.Invoke(null, ["Chaos Orb"])!);
        Assert.False((bool)method!.Invoke(null, ["Unique Ring"])!);
    }

    [Fact]
    public void TargetCycleMs_Is50Milliseconds()
    {
        const double expected = 50;
        var field = typeof(LeaguePricingWorker).GetField("TargetCycleMs",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var val = (double)field.GetValue(null)!;
        Assert.Equal(expected, val);
    }

    [Fact]
    public void StaleRenderTimeout_Is180Milliseconds()
    {
        var field = typeof(LeaguePricingWorker).GetField("StaleRenderTimeout",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (TimeSpan)field!.GetValue(null)!;
        Assert.Equal(180, value.TotalMilliseconds);
    }

    [Fact]
    public void UnpriceableExactNames_ContainsVerisiumPile()
    {
        var field = typeof(LeaguePricingWorker).GetField("UnpriceableExactNames",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (string[])field!.GetValue(null)!;
        Assert.Contains("Verisium Pile", value);
    }

    [Fact]
    public void PricedUncutPrefixes_ContainsAllGems()
    {
        var field = typeof(LeaguePricingWorker).GetField("PricedUncutPrefixes",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (string[])field!.GetValue(null)!;
        Assert.Contains("Uncut Skill Gem", value);
        Assert.Contains("Uncut Support Gem", value);
        Assert.Contains("Uncut Spirit Gem", value);
    }

    [Fact]
    public void BuildUnpriceableBanner_EmptyList_ReturnsNull()
    {
        var result = InvokeBuildUnpriceableBanner([]);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_SkillGem_Detected()
    {
        var result = InvokeBuildUnpriceableBanner(SkillGemName);
        Assert.NotNull(result);
        Assert.Contains("can't be priced", result!, StringComparison.OrdinalIgnoreCase);
    }

    private static string? InvokeBuildUnpriceableBanner(string[] names)
    {
        var method = typeof(LeaguePricingWorker).GetMethod("BuildUnpriceableBanner",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null) return null;
        return method.Invoke(null, [names, null]) as string;
    }
}
