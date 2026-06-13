using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class UpdateCheckerDownloadTests
{
    [Fact]
    public void TryParseVersion_VPrefix_StrippedCorrectly()
    {
        var input = "v1.2.3";
        var stripped = input.TrimStart('v', 'V');
        Assert.Equal("1.2.3", stripped);

        var result = UpdateChecker.TryParseVersion(stripped, out var version);
        Assert.True(result);
        Assert.Equal(1, version.Major);
        Assert.Equal(2, version.Minor);
        Assert.Equal(3, version.Build);
    }

    [Fact]
    public void TryParseVersion_CapitalVPrefix_StrippedCorrectly()
    {
        var input = "V1.0.0";
        var stripped = input.TrimStart('v', 'V');
        Assert.Equal("1.0.0", stripped);

        var result = UpdateChecker.TryParseVersion(stripped, out var version);
        Assert.True(result);
        Assert.Equal(1, version.Major);
    }

    [Fact]
    public void TryParseVersion_WithBuildMetadata_PlusStripped()
    {
        var input = "1.2.3+abc123";
        var plusIndex = input.IndexOf('+');
        var stripped = plusIndex >= 0 ? input[..plusIndex] : input;
        Assert.Equal("1.2.3", stripped);

        var result = UpdateChecker.TryParseVersion(stripped, out var version);
        Assert.True(result);
        Assert.Equal(1, version.Major);
        Assert.Equal(2, version.Minor);
        Assert.Equal(3, version.Build);
    }

    [Fact]
    public void TryParseVersion_VersionWithVPrefix_InvalidWithoutStripping()
    {
        var result = UpdateChecker.TryParseVersion("v1.2.3", out _);
        Assert.False(result);
    }

    [Fact]
    public void TryParseVersion_VersionWithBuildSuffix_InvalidWithoutStripping()
    {
        var result = UpdateChecker.TryParseVersion("1.2.3+abc", out _);
        Assert.False(result);
    }

    [Fact]
    public void VersionComparison_SameVersion_Equal()
    {
        Assert.True(UpdateChecker.TryParseVersion("1.0.0", out var v1));
        Assert.True(UpdateChecker.TryParseVersion("1.0.0", out var v2));
        Assert.Equal(0, v1.CompareTo(v2));
    }

    [Fact]
    public void VersionComparison_NewerMajor_Greater()
    {
        Assert.True(UpdateChecker.TryParseVersion("2.0.0", out var v1));
        Assert.True(UpdateChecker.TryParseVersion("1.99.99", out var v2));
        Assert.True(v1 > v2);
    }

    [Fact]
    public void VersionComparison_NewerMinor_Greater()
    {
        Assert.True(UpdateChecker.TryParseVersion("1.10.0", out var v1));
        Assert.True(UpdateChecker.TryParseVersion("1.9.99", out var v2));
        Assert.True(v1 > v2);
    }

    [Fact]
    public void VersionComparison_NewerBuild_Greater()
    {
        Assert.True(UpdateChecker.TryParseVersion("1.0.100", out var v1));
        Assert.True(UpdateChecker.TryParseVersion("1.0.99", out var v2));
        Assert.True(v1 > v2);
    }

    [Fact]
    public void VersionComparison_MajorDominates()
    {
        Assert.True(UpdateChecker.TryParseVersion("2.0.0", out var v1));
        Assert.True(UpdateChecker.TryParseVersion("1.999.999", out var v2));
        Assert.True(v1 > v2);
    }

    [Fact]
    public void TryParseVersion_NullOrWhitespace_ReturnsFalse()
    {
        Assert.False(UpdateChecker.TryParseVersion("", out _));
        Assert.False(UpdateChecker.TryParseVersion("   ", out _));
    }

    [Fact]
    public void TryParseVersion_NonNumericVersion_ReturnsFalse()
    {
        Assert.False(UpdateChecker.TryParseVersion("abc.def.ghi", out _));
    }

    [Fact]
    public void TryParseVersion_SingleComponent_ReturnsFalse()
    {
        Assert.False(UpdateChecker.TryParseVersion("1", out _));
    }

    [Fact]
    public void TryParseVersion_TwoComponents_ReturnsFalse()
    {
        Assert.False(UpdateChecker.TryParseVersion("1.2", out _));
    }

    [Fact]
    public void TryParseVersion_FourComponents_ReturnsFalse()
    {
        Assert.False(UpdateChecker.TryParseVersion("1.2.3.4", out _));
    }

    [Fact]
    public void TryParseVersion_Zeroes_Works()
    {
        var result = UpdateChecker.TryParseVersion("0.0.0", out var version);
        Assert.True(result);
        Assert.Equal(0, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(0, version.Build);
    }
}
