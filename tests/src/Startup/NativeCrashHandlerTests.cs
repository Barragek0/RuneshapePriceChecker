using System.Diagnostics;
using System.IO;
using System.Reflection;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public sealed class NativeCrashHandlerTests : IDisposable
{
    private readonly string _crashLogsDir;
    private readonly string _simExe;

    public NativeCrashHandlerTests()
    {
        var simDir = GetSimulatorDir();
        _crashLogsDir = Path.Combine(simDir, "logs");
        _simExe = Path.Combine(simDir, "CrashSimulator.exe");

        if (Directory.Exists(_crashLogsDir))
            foreach (var f in Directory.GetFiles(_crashLogsDir, "*-native-crash.txt"))
                File.Delete(f);
    }

    [Fact]
    public async Task AccessViolationRead_ProducesNativeCrashLog()
    {
        if (!File.Exists(_simExe))
        {
            Assert.Fail($"CrashSimulator not found at {_simExe}. Build the solution first.");
            return;
        }

        _ = await RunAndWait(_simExe, "av-read");

        var logs = Directory.GetFiles(_crashLogsDir, "*-native-crash.txt");
        Assert.NotEmpty(logs);

        var content = File.ReadAllText(logs[0]);
        Assert.Contains("Crash Report", content);
        Assert.Contains("Code:", content);
        Assert.Contains("-- Registers --", content);
        Assert.Contains("RAX", content);
    }

    [Fact]
    public async Task AccessViolationWrite_ProducesNativeCrashLog()
    {
        if (!File.Exists(_simExe))
        {
            Assert.Fail($"CrashSimulator not found at {_simExe}. Build the solution first.");
            return;
        }

        _ = await RunAndWait(_simExe, "av-write");

        var logs = Directory.GetFiles(_crashLogsDir, "*-native-crash.txt");
        Assert.NotEmpty(logs);

        var content = File.ReadAllText(logs[0]);
        Assert.Contains("ACCESS_VIOLATION", content);
        Assert.Contains("WRITE to", content);
        Assert.Contains("-- Registers --", content);
    }

    [Fact]
    public async Task StackOverflow_ProducesNativeCrashLog()
    {
        if (!File.Exists(_simExe))
        {
            Assert.Fail($"CrashSimulator not found at {_simExe}. Build the solution first.");
            return;
        }

        _ = await RunAndWait(_simExe, "stack");

        var logs = Directory.GetFiles(_crashLogsDir, "*-native-crash.txt");
        Assert.NotEmpty(logs);

        var content = File.ReadAllText(logs[0]);
        Assert.Contains("STACK_OVERFLOW", content);
        Assert.Contains("-- Registers --", content);
    }

    [Fact]
    public async Task AllCrashLogs_ContainOffsetAndCallstack()
    {
        if (!File.Exists(_simExe))
        {
            Assert.Fail($"CrashSimulator not found at {_simExe}. Build the solution first.");
            return;
        }

        foreach (var crashType in new[] { "av-read", "av-write", "stack" })
        {
            _ = await RunAndWait(_simExe, crashType);

            var logs = Directory.GetFiles(_crashLogsDir, "*-native-crash.txt");
            Assert.NotEmpty(logs);

            var content = File.ReadAllText(logs[0]);
            Assert.Contains("Exe base:", content);
            Assert.Contains("Offset:", content);
            Assert.Contains("-- Callstack --", content);

            foreach (var f in logs)
                File.Delete(f);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_crashLogsDir))
            try { Directory.Delete(_crashLogsDir, recursive: true); } catch { }
    }

    private static async Task<int> RunAndWait(string exe, string arg)
    {
        var psi = new ProcessStartInfo(exe, arg)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["DOTNET_EnableCrashReport"] = "0";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start CrashSimulator");

        try
        {
            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"CrashSimulator '{arg}' did not exit within 30 seconds (WER dialog may be blocking).");
        }

        return proc.ExitCode;
    }

    private static string GetSimulatorDir()
    {
        var testDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Cannot determine test assembly location");

        var search = "tests" + Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        var idx = testDir.LastIndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            throw new InvalidOperationException($"Cannot locate CrashSimulator from test path: {testDir}");

        var rest = testDir[(idx + search.Length)..];
        return Path.Combine(testDir[..idx], "tests", "CrashSimulator", "bin", rest);
    }

}
