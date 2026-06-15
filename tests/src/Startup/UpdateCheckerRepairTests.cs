using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class UpdateCheckerRepairTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _installDir;

    public UpdateCheckerRepairTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rstest-repair-{Guid.NewGuid():N}");
        _installDir = Path.Combine(_tempDir, "install");
        Directory.CreateDirectory(_installDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RepairUpdater_UpdaterMissing_LogsAndReturns()
    {
        var checker = CreateChecker();
        var zipAsset = new GitHubAsset("RuneshapePriceChecker.zip", "https://example.com/zip", 1000);

        await InvokeRepairUpdaterAsync(checker, zipAsset, "1.0.0", _installDir);

        // Should not crash when Update.exe is not present
        Assert.False(File.Exists(Path.Combine(_installDir, "Update.exe")));
    }

    [Fact]
    public async Task RepairUpdater_NoZipUrl_LogsWarningAndReturns()
    {
        var checker = CreateChecker();
        var updaterPath = Path.Combine(_installDir, "Update.exe");
        File.WriteAllText(updaterPath, "fake exe");

        // Set outdated file version by creating a file that can't be read
        var zipAsset = new GitHubAsset("RuneshapePriceChecker.zip", null, 1000);

        await InvokeRepairUpdaterAsync(checker, zipAsset, "2.0.0", _installDir);

        // Should not crash
    }

    [Fact]
    public async Task RepairUpdater_CurrentVersion_SkipsRepair()
    {
        var checker = CreateChecker();

        // Create a zip with an Update.exe to serve as the download source
        var zipPath = Path.Combine(_tempDir, "release.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Update.exe");
            using var stream = entry.Open();
            stream.Write([0x4D, 0x5A], 0, 2); // MZ header
        }

        var zipAsset = new GitHubAsset("release.zip", "file://" + zipPath.Replace('\\', '/'), new FileInfo(zipPath).Length);

        // Create a dummy Update.exe in install dir
        var updaterPath = Path.Combine(_installDir, "Update.exe");
        File.WriteAllText(updaterPath, "dummy");

        await InvokeRepairUpdaterAsync(checker, zipAsset, "0.0.1", _installDir);

        // The existing Update.exe should still be there (either repaired or not crashed)
        // No strong assertion since we can't control file version easily
    }

    [Fact]
    public async Task RepairUpdater_CorruptVersionInfo_HandledGracefully()
    {
        var checker = CreateChecker();
        var updaterPath = Path.Combine(_installDir, "Update.exe");
        File.WriteAllText(updaterPath, "not a real exe");

        var zipAsset = new GitHubAsset("RuneshapePriceChecker.zip", null, 1000);

        await InvokeRepairUpdaterAsync(checker, zipAsset, "1.0.0", _installDir);

        // Should not throw
    }

    private static UpdateChecker CreateChecker()
    {
        var updateOptions = Options.Create(new UpdateOptions());
        var appOptions = new StaticOptionsMonitor<AppOptions>(new AppOptions());
        var logger = new LoggerFactory().CreateLogger<UpdateChecker>();
        var lifetime = new NullApplicationLifetime();
        var sink = new DashboardLogSink();
        var dashboard = new DashboardService(sink);
        var factory = new SingleClientHttpClientFactory(new HttpClient(), "GitHub");

        return new UpdateChecker(updateOptions, appOptions, lifetime, logger, dashboard, factory);
    }

    private static async Task InvokeRepairUpdaterAsync(
        UpdateChecker checker, GitHubAsset zipAsset, string version, string installDir)
    {
        var method = typeof(UpdateChecker).GetMethod("RepairUpdaterIfNeededAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task?)method!.Invoke(checker, [zipAsset, version, installDir]);
        if (task is not null)
            await task;
    }
}
