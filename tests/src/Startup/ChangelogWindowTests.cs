using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class ChangelogWindowTests
{
    [Fact]
    public void ChangelogWindow_Type_Exists()
    {
        var type = typeof(ChangelogWindow);
        Assert.NotNull(type);
    }
}
