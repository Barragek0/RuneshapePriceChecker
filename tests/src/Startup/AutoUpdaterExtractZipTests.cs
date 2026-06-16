using System.IO;
using System.IO.Compression;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class AutoUpdaterExtractZipTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _destDir;

    public AutoUpdaterExtractZipTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rstest-zip-{Guid.NewGuid():N}");
        _destDir = Path.Combine(_tempDir, "dest");
        _ = Directory.CreateDirectory(_tempDir);
        _ = Directory.CreateDirectory(_destDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ExtractZip_ExtractsAllFiles()
    {
        var zipPath = CreateTestZip(new Dictionary<string, string>
        {
            ["RuneshapePriceChecker.exe"] = "fake-exe",
            ["RuneshapePriceChecker.dll"] = "fake-dll",
            ["README.txt"] = "readme content"
        });

        ExtractZip(zipPath, _destDir, "Updater.exe");

        Assert.True(File.Exists(Path.Combine(_destDir, "RuneshapePriceChecker.exe")));
        Assert.True(File.Exists(Path.Combine(_destDir, "RuneshapePriceChecker.dll")));
        Assert.True(File.Exists(Path.Combine(_destDir, "README.txt")));
    }

    [Fact]
    public void ExtractZip_PreservesExistingAppsettings()
    {
        var existingConfigPath = Path.Combine(_destDir, "appsettings.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(existingConfigPath)!);
        File.WriteAllText(existingConfigPath, """{"App":{"LogLevel":"Debug"}}""");

        var zipPath = CreateTestZip(new Dictionary<string, string>
        {
            ["appsettings.json"] = """{"App":{"LogLevel":"Warning"}}""",
            ["RuneshapePriceChecker.exe"] = "fake-exe"
        });

        ExtractZip(zipPath, _destDir, "Updater.exe");

        var configContent = File.ReadAllText(existingConfigPath);
        Assert.Contains("Debug", configContent);
        Assert.DoesNotContain("Warning", configContent);
    }

    [Fact]
    public void ExtractZip_WritesNewAppsettings_WhenNotPresent()
    {
        var zipPath = CreateTestZip(new Dictionary<string, string>
        {
            ["appsettings.json"] = """{"App":{"LogLevel":"Warning"}}""",
            ["RuneshapePriceChecker.exe"] = "fake-exe"
        });

        ExtractZip(zipPath, _destDir, "Updater.exe");

        var configPath = Path.Combine(_destDir, "appsettings.json");
        Assert.True(File.Exists(configPath));
        Assert.Contains("Warning", File.ReadAllText(configPath));
    }

    [Fact]
    public void ExtractZip_RenamesSelfExeToNew()
    {
        var zipPath = CreateTestZip(new Dictionary<string, string>
        {
            ["RuneshapePriceChecker.exe"] = "fake-exe",
            ["Update.exe"] = "updater-content"
        });

        ExtractZip(zipPath, _destDir, "Update.exe");

        Assert.True(File.Exists(Path.Combine(_destDir, "Update.exe.new")));
        Assert.False(File.Exists(Path.Combine(_destDir, "Update.exe")));
    }

    [Fact]
    public void ExtractZip_DoesNotRenameOtherExe()
    {
        var zipPath = CreateTestZip(new Dictionary<string, string>
        {
            ["RuneshapePriceChecker.exe"] = "fake-exe",
            ["Update.exe"] = "updater-content"
        });

        ExtractZip(zipPath, _destDir, "SomeOtherUpdater.exe");

        Assert.True(File.Exists(Path.Combine(_destDir, "RuneshapePriceChecker.exe")));
        Assert.True(File.Exists(Path.Combine(_destDir, "Update.exe")));
        Assert.False(File.Exists(Path.Combine(_destDir, "RuneshapePriceChecker.exe.new")));
    }

    [Fact]
    public void ExtractZip_SkipsDirectoryEntries()
    {
        var zipPath = Path.Combine(_tempDir, "test.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("subdir/");
            var fileEntry = archive.CreateEntry("subdir/file.txt");
            using var sw = new StreamWriter(fileEntry.Open());
            sw.Write("content");
        }

        ExtractZip(zipPath, _destDir, "Updater.exe");

        // File is extracted (directory created implicitly)
        Assert.True(File.Exists(Path.Combine(_destDir, "subdir", "file.txt")));
        // The directory entry itself was skipped — no exception thrown for it
    }

    [Fact]
    public void ExtractZip_CreatesNestedDirectories()
    {
        var zipPath = CreateTestZip(new Dictionary<string, string>
        {
            ["deep/nested/path/file.txt"] = "content"
        });

        ExtractZip(zipPath, _destDir, "Updater.exe");

        Assert.True(File.Exists(Path.Combine(_destDir, "deep", "nested", "path", "file.txt")));
    }

    [Fact]
    public void ExtractZip_EmptyZip_DoesNotThrow()
    {
        var zipPath = Path.Combine(_tempDir, "empty.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // No entries
        }

        ExtractZip(zipPath, _destDir, "Updater.exe");

        // Should not throw
    }

    [Fact]
    public void ExtractZip_OverwritesExistingFiles()
    {
        var existingPath = Path.Combine(_destDir, "file.txt");
        File.WriteAllText(existingPath, "old content");

        var zipPath = CreateTestZip(new Dictionary<string, string>
        {
            ["file.txt"] = "new content"
        });

        ExtractZip(zipPath, _destDir, "Updater.exe");

        Assert.Equal("new content", File.ReadAllText(existingPath));
    }

    private string CreateTestZip(Dictionary<string, string> files)
    {
        var zipPath = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var sw = new StreamWriter(entry.Open());
                sw.Write(content);
            }
        }
        return zipPath;
    }

    // Mirrors the updater's ExtractZip local function logic exactly
    private static void ExtractZip(string zipPath, string destinationDir, string selfExe)
    {
        var failed = new List<string>();
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
            if (entry.Name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                var destConfig = Path.Combine(destinationDir, entry.FullName);
                if (File.Exists(destConfig)) continue;
            }

            var destPath = Path.Combine(destinationDir, entry.FullName);
            if (entry.Name.Equals(selfExe, StringComparison.OrdinalIgnoreCase))
                destPath += ".new";

            var destDir = Path.GetDirectoryName(destPath)!;
            if (!Directory.Exists(destDir)) _ = Directory.CreateDirectory(destDir);

            var extracted = false;
            for (var retry = 0; retry < 5; retry++)
            {
                try
                {
                    if (File.Exists(destPath))
                    {
                        try { File.Delete(destPath); } catch { }
                    }
                    entry.ExtractToFile(destPath, overwrite: true);
                    extracted = true;
                    break;
                }
                catch (IOException)
                {
                    if (retry < 4) { Thread.Sleep(500); }
                }
            }

            if (!extracted)
                failed.Add(entry.FullName);
        }

        if (failed.Count > 0)
            throw new InvalidOperationException($"{failed.Count} file(s) could not be extracted: {string.Join(", ", failed)}");
    }
}
