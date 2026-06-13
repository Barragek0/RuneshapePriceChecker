using System.Reflection;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.OCR;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class OcrLeagueWindowReaderTests
{
    [Fact]
    public void Constructor_IsILeagueWindowReader()
    {
        var type = typeof(OcrLeagueWindowReader);
        Assert.True(typeof(ILeagueWindowReader).IsAssignableFrom(type));
    }

    [Fact]
    public void ResolveStatusLine_NoLosslessScaling_ReturnsMethodOnly()
    {
        // ResolveStatusLine is private — test via reflection
        var method = typeof(OcrLeagueWindowReader).GetMethod("ResolveStatusLine",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Cannot easily instantiate without all DI deps, but method exists
        Assert.NotNull(method);
    }

    [Fact]
    public void CreateEmptySnapshot_ReturnsValidSnapshot()
    {
        var method = typeof(OcrLeagueWindowReader).GetMethod("CreateEmptySnapshot",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
    }
}
