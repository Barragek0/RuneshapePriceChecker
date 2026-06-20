using System.Reflection;
using System.Text.Json.Nodes;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class AppSettingsBootstrapperTests
{
    private static readonly MethodInfo DeepMergeDefaultsMethod = typeof(AppSettingsBootstrapper)
        .GetMethod("DeepMergeDefaults", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo RenameKeyMethod = typeof(AppSettingsBootstrapper)
        .GetMethod("RenameKey", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool DeepMergeDefaults(JsonNode existing, JsonNode defaults)
    {
        return (bool)DeepMergeDefaultsMethod.Invoke(null, [existing, defaults])!;
    }

    private static bool RenameKey(JsonNode node, string oldKey, string newKey)
    {
        return (bool)RenameKeyMethod.Invoke(null, [node, oldKey, newKey])!;
    }

    [Fact]
    public void DeepMerge_MissingTopLevelKey_AddsIt()
    {
        var existing = JsonNode.Parse("""{"App":{"LogLevel":"Debug"}}""")!;
        var defaults = JsonNode.Parse("""{"App":{"LogLevel":"Info"},"Pricing":{"League":"Standard"}}""")!;

        var missing = DeepMergeDefaults(existing, defaults);

        Assert.True(missing);
        Assert.NotNull(existing["Pricing"]);
        Assert.Equal("Standard", existing["Pricing"]!["League"]!.GetValue<string>());
    }

    [Fact]
    public void DeepMerge_AllKeysPresent_ReturnsFalse()
    {
        var existing = JsonNode.Parse("""{"App":{"LogLevel":"Debug"}}""")!;
        var defaults = JsonNode.Parse("""{"App":{"LogLevel":"Info"}}""")!;

        var missing = DeepMergeDefaults(existing, defaults);

        Assert.False(missing);
    }

    [Fact]
    public void DeepMerge_MissingNestedKey_AddsIt()
    {
        var existing = JsonNode.Parse("""{"App":{"LogLevel":"Debug"}}""")!;
        var defaults = JsonNode.Parse("""{"App":{"LogLevel":"Info","NewSetting":"value"}}""")!;

        var missing = DeepMergeDefaults(existing, defaults);

        Assert.True(missing);
        Assert.Equal("value", existing["App"]!["NewSetting"]!.GetValue<string>());
    }

    [Fact]
    public void DeepMerge_ExistingNestedKey_PreservesValue()
    {
        var existing = JsonNode.Parse("""{"App":{"LogLevel":"Debug"}}""")!;
        var defaults = JsonNode.Parse("""{"App":{"LogLevel":"Info"}}""")!;

        _ = DeepMergeDefaults(existing, defaults);

        Assert.Equal("Debug", existing["App"]!["LogLevel"]!.GetValue<string>());
    }

    [Fact]
    public void DeepMerge_MultipleMissingKeys_AddsAll()
    {
        var existing = JsonNode.Parse("""{}""")!;
        var defaults = JsonNode.Parse("""{"A":"1","B":"2","C":"3"}""")!;

        var missing = DeepMergeDefaults(existing, defaults);

        Assert.True(missing);
        Assert.Equal("1", existing["A"]!.GetValue<string>());
        Assert.Equal("2", existing["B"]!.GetValue<string>());
        Assert.Equal("3", existing["C"]!.GetValue<string>());
    }

    [Fact]
    public void DeepMerge_ExistingPreservesNonDefaultValues()
    {
        var existing = JsonNode.Parse("""{"Pricing":{"League":"Custom"}}""")!;
        var defaults = JsonNode.Parse("""{"Pricing":{"League":"Standard","RedThreshold":0.5}}""")!;

        var missing = DeepMergeDefaults(existing, defaults);

        Assert.True(missing);
        Assert.Equal("Custom", existing["Pricing"]!["League"]!.GetValue<string>());
        Assert.Equal(0.5m, existing["Pricing"]!["RedThreshold"]!.GetValue<decimal>());
    }

    [Fact]
    public void RenameKey_OldKeyExists_RenamesAndReturnsTrue()
    {
        var node = JsonNode.Parse("""{"oldName":"value"}""")!;

        var result = RenameKey(node, "oldName", "newName");

        Assert.True(result);
        Assert.Null(node["oldName"]);
        Assert.Equal("value", node["newName"]!.GetValue<string>());
    }

    [Fact]
    public void RenameKey_OldKeyMissing_ReturnsFalse()
    {
        var node = JsonNode.Parse("""{"other":"value"}""")!;

        var result = RenameKey(node, "oldName", "newName");

        Assert.False(result);
        Assert.Null(node["newName"]);
    }
}
