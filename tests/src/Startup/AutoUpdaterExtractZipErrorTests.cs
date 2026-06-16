using System.IO;
using System.IO.Compression;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class AutoUpdaterExtractZipErrorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _destDir;

    public AutoUpdaterExtractZipErrorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rstest-ziperr-{Guid.NewGuid():N}");
        _destDir = Path.Combine(_tempDir, "dest");
        _ = Directory.CreateDirectory(_destDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ExtractZip_EmptyZip_NoFilesExtracted()
    {
        var zipPath = Path.Combine(_tempDir, "empty.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create)) { }

        MirrorExtractZip(zipPath, _destDir, "Updater.exe");
        Assert.Empty(Directory.GetFiles(_destDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ExtractZip_NestedPaths_Created()
    {
        var zipPath = Path.Combine(_tempDir, "nested.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("a/b/c/d/file.txt");
            using var sw = new StreamWriter(entry.Open());
            sw.Write("deep");
        }

        MirrorExtractZip(zipPath, _destDir, "Updater.exe");
        Assert.True(File.Exists(Path.Combine(_destDir, "a", "b", "c", "d", "file.txt")));
    }

    private static void MirrorExtractZip(string zipPath, string destDir, string selfExe)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
            if (entry.Name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                var dc = Path.Combine(destDir, entry.FullName);
                if (File.Exists(dc)) continue;
            }
            var dp = Path.Combine(destDir, entry.FullName);
            if (entry.Name.Equals(selfExe, StringComparison.OrdinalIgnoreCase)) dp += ".new";
            var dd = Path.GetDirectoryName(dp)!;
            if (!Directory.Exists(dd)) _ = Directory.CreateDirectory(dd);
            entry.ExtractToFile(dp, true);
        }
    }
}