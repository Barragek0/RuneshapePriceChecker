using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class VersionParsingExpandedTests
{
    [Fact]
    public void TryParseVersion_PlusBuildMetadata_StrippedBeforeParse()
    {
        var input = "1.2.3+abc123";
        var plus = input.IndexOf('+');
        var stripped = plus >= 0 ? input[..plus] : input;
        Assert.True(UpdateChecker.TryParseVersion(stripped, out var v));
        Assert.Equal(1, v.Major);
    }

    [Fact]
    public void VersionComparison_ZeroToMax_OrderedCorrectly()
    {
        Assert.True(UpdateChecker.TryParseVersion("0.0.0", out var min));
        Assert.True(UpdateChecker.TryParseVersion("99.99.999", out var max));
        Assert.True(max > min);
    }

    [Fact]
    public void VersionComparison_SameVersion_Equals()
    {
        Assert.True(UpdateChecker.TryParseVersion("0.2.2", out var old));
        Assert.True(UpdateChecker.TryParseVersion("0.2.2", out var same));
        Assert.True(old >= same);
        Assert.True(old <= same);
    }
}