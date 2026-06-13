using RuneshapePriceChecker.Overlay;
using Xunit;

namespace RuneshapePriceChecker.Tests.Overlay;

public class BannerFormTests
{
    [Fact]
    public void BannerForm_IsFormSubclass()
    {
        var type = typeof(BannerForm);
        Assert.True(typeof(Form).IsAssignableFrom(type));
    }
}
