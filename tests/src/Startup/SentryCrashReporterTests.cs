using System.IO;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public sealed class SentryCrashReporterTests
{
    [Fact]
    public void DisabledReporter_DoesNotCreateCache()
    {
        var database = Path.Combine(SentryPaths.RootDirectory, $"database-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(database);
        File.WriteAllText(Path.Combine(database, "stale"), "stale");

        using var reporter = SentryCrashReporter.Start(new SentryCrashReporterOptions(
            false, string.Empty, "test", "test", database,
            SentryPaths.HandlerPath,
            string.Empty, string.Empty, string.Empty, string.Empty));

        Assert.False(reporter.IsEnabled);
        Assert.False(Directory.Exists(database));
    }

}
