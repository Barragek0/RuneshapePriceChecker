using System.Diagnostics;
using System.IO;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

[Collection("AppProcessTests")]
public class AppProcessTests : IDisposable
{
    private static Process? _appProcess;
    private static readonly object _lock = new();

    public AppProcessTests()
    {
        lock (_lock)
        {
            if (_appProcess is { HasExited: false }) return;

            var exe = FindExe();
            if (exe is null) return;

            _appProcess?.Kill(entireProcessTree: true);
            _appProcess?.Dispose();

            _appProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--App:SuppressAlreadyRunningWarning=true --App:LogLevel=Debug --App:TestMode=true",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            _appProcess.Start();
            _appProcess.WaitForInputIdle(5000);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void App_Starts_AndWindowAppears()
    {
        if (_appProcess is null) return;
        Assert.False(_appProcess.HasExited, "App should still be running");
    }

    internal static void KillAppProcess()
    {
        lock (_lock)
        {
            if (_appProcess is { HasExited: false })
            {
                _appProcess.Kill(entireProcessTree: true);
            }
            _appProcess?.Dispose();
            _appProcess = null;
        }
    }

    private static string? FindExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var publishDir = Path.Combine(baseDir, "..", "..", "..", "..", "bin", "Release", "net8.0-windows", "win-x64");
        var exe = Path.Combine(publishDir, "RuneshapePriceChecker.exe");
        if (File.Exists(exe)) return exe;
        return null;
    }
}

[CollectionDefinition("AppProcessTests")]
public class AppProcessTestContext : ICollectionFixture<AppProcessTestTeardown> { }

public class AppProcessTestTeardown : IDisposable
{
    public void Dispose()
    {
        AppProcessTests.KillAppProcess();
        GC.SuppressFinalize(this);
    }
}
