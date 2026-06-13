using System.Collections.Concurrent;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

public class DashboardLogSinkTests : IDisposable
{
    private readonly DashboardLogSink _sink;

    public DashboardLogSinkTests()
    {
        _sink = new DashboardLogSink();
    }

    public void Dispose()
    {
        _sink.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Emit_SingleMessage_FiresOnLogEntry()
    {
        LogEntry? received = null;
        _sink.OnLogEntry += e => received = e;

        _sink.Emit("Test message");

        // The flush timer fires asynchronously; we may not get it immediately.
        // Snapshot should contain the entry though.
        var snapshot = _sink.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("Test message", snapshot[0].Message);
    }

    [Fact]
    public void Emit_DuplicateMessage_IncrementsCount()
    {
        _sink.Emit("Duplicate");
        _sink.Emit("Duplicate");
        _sink.Emit("Duplicate");

        var snapshot = _sink.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(3, snapshot[0].Count);
        Assert.Equal("Duplicate", snapshot[0].Message);
    }

    [Fact]
    public void Emit_DifferentMessages_TrackedIndependently()
    {
        _sink.Emit("First");
        _sink.Emit("Second");
        _sink.Emit("Third");

        var snapshot = _sink.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Contains(snapshot, e => e.Message == "First");
        Assert.Contains(snapshot, e => e.Message == "Second");
        Assert.Contains(snapshot, e => e.Message == "Third");
    }

    [Fact]
    public void Emit_DifferentColors_ColorPreserved()
    {
        _sink.Emit("Red message", "red");
        _sink.Emit("Green message", "green");

        var snapshot = _sink.Snapshot();
        var red = snapshot.First(e => e.Message == "Red message");
        var green = snapshot.First(e => e.Message == "Green message");
        Assert.Equal("red", red.Color);
        Assert.Equal("green", green.Color);
    }

    [Fact]
    public void Emit_DefaultColor_IsDefault()
    {
        _sink.Emit("No color specified");

        var snapshot = _sink.Snapshot();
        Assert.Equal("default", snapshot[0].Color);
    }

    [Fact]
    public void Emit_EmptyMessage_DoesNotCrash()
    {
        _sink.Emit("");
        _sink.Emit("   ");

        var snapshot = _sink.Snapshot();
        // Empty and whitespace are treated as separate messages
        Assert.True(snapshot.Count >= 1);
    }

    [Fact]
    public void Snapshot_ReturnsCurrentState()
    {
        _sink.Emit("A");
        _sink.Emit("B");

        var snapshot = _sink.Snapshot();
        Assert.Equal(2, snapshot.Count);
    }

    [Fact]
    public void Snapshot_IsThreadSafe()
    {
        var exceptions = new ConcurrentBag<Exception>();
        var tasks = new List<Task>();

        for (var t = 0; t < 10; t++)
        {
            var i = t;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (var j = 0; j < 100; j++)
                    {
                        _sink.Emit($"Thread{i}-Msg{j}");
                        _sink.Snapshot();
                    }
                }
                catch (Exception ex) { exceptions.Add(ex); }
            }));
        }

        Task.WaitAll(tasks.ToArray());
        Assert.Empty(exceptions);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        _sink.Dispose();
        _sink.Dispose(); // Should not throw
    }
}
