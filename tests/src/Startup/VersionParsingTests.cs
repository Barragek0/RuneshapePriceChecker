using Xunit;
using System.Text.RegularExpressions;
using System.Globalization;

namespace RuneshapePriceChecker.Tests.Startup;

public class VersionParsingTests
{
    private static readonly Regex SemverPattern = new(@"^(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("0.1.3", 0, 1, 3)]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("0.99.99", 0, 99, 99)]
    [InlineData("10.20.30", 10, 20, 30)]
    public void ParseSemver_ValidInputs(string input, int major, int minor, int patch)
    {
        var match = SemverPattern.Match(input);

        Assert.True(match.Success);
        Assert.Equal(major, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(minor, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        Assert.Equal(patch, int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("v0.1.3")]
    [InlineData("0.1")]
    [InlineData("preview")]
    [InlineData("")]
    [InlineData("1.2.3.4")]
    public void ParseSemver_InvalidInputs_ReturnsNoMatch(string input)
    {
        var match = SemverPattern.Match(input);

        Assert.False(match.Success);
    }

    [Fact]
    public void VersionComparison_MajorDominates()
    {
        Assert.True(new Version(1, 0, 0) > new Version(0, 99, 99));
    }

    [Fact]
    public void VersionComparison_MinorDominates()
    {
        Assert.True(new Version(0, 2, 0) > new Version(0, 1, 99));
    }

    [Fact]
    public void VersionComparison_PatchDominates()
    {
        Assert.True(new Version(0, 1, 10) > new Version(0, 1, 9));
    }
}
