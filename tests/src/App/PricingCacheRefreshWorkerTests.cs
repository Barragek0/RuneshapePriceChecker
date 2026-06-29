using RuneshapePriceChecker.App;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

public class PricingCacheRefreshWorkerTests
{
    [Fact]
    public void PricingCacheRefreshWorker_IsBackgroundService()
    {
        var type = typeof(PricingCacheRefreshWorker);
        Assert.NotNull(type);
        Assert.True(typeof(Microsoft.Extensions.Hosting.BackgroundService).IsAssignableFrom(type));
    }
}
