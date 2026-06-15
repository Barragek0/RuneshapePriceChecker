using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Overlay;
using Xunit;

namespace RuneshapePriceChecker.Tests.Overlay;

public class ConsoleOverlayRendererTests
{
    [Fact]
    public void ConsoleOverlayRenderer_ImplementsIOverlayRenderer()
    {
        var type = typeof(PricingOverlayRenderer);
        Assert.True(typeof(IOverlayRenderer).IsAssignableFrom(type));
    }
}
