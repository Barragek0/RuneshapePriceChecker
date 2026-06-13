using RuneshapePriceChecker.Configuration;
using Xunit;

namespace RuneshapePriceChecker.Tests.Configuration;

public class WindowOptionsTests
{
    [Fact]
    public void Default_InitialSetupComplete_IsFalse()
    {
        var options = new WindowOptions();
        Assert.False(options.InitialSetupComplete);
    }

    [Fact]
    public void Default_NullableFields_AreNull()
    {
        var options = new WindowOptions();
        Assert.Null(options.CustomOffsetX);
        Assert.Null(options.CustomOffsetY);
        Assert.Null(options.CustomWidth);
        Assert.Null(options.CustomHeight);
    }

    [Fact]
    public void Can_SetProperties()
    {
        var options = new WindowOptions
        {
            InitialSetupComplete = true,
            CustomOffsetX = 100,
            CustomOffsetY = 200,
            CustomWidth = 300,
            CustomHeight = 400
        };
        Assert.True(options.InitialSetupComplete);
        Assert.Equal(100, options.CustomOffsetX);
        Assert.Equal(200, options.CustomOffsetY);
        Assert.Equal(300, options.CustomWidth);
        Assert.Equal(400, options.CustomHeight);
    }
}