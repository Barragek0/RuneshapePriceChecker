using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("0.9.9", 0, 9, 9)]
    [InlineData("10.20.300", 10, 20, 300)]
    public void TryParseVersion_ValidSemver_ReturnsTrue(string input, int major, int minor, int build)
    {
        var result = UpdateChecker.TryParseVersion(input, out var version);

        Assert.True(result);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    public void TryParseVersion_InvalidInput_ReturnsFalse(string input)
    {
        var result = UpdateChecker.TryParseVersion(input, out _);
        Assert.False(result);
    }
}
