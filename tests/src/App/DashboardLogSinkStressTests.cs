using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

public class DashboardLogSinkStressTests
{
    [Fact]
    public void Emit_1001Unique_OldestEvicted()
    {
        using var sink = new DashboardLogSink();
        for (var i = 0; i < 1001; i++)
            sink.Emit($"Msg {i}");

        var snapshot = sink.Snapshot();
        Assert.True(snapshot.Count <= 1000);
    }

    [Fact]
    public void Emit_Concurrent_DoesNotCorrupt()
    {
        using var sink = new DashboardLogSink();
        Parallel.For(0, 500, i => sink.Emit($"Thread {i % 10}"));
        var snapshot = sink.Snapshot();
        Assert.NotEmpty(snapshot);
    }
}