using System.IO;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class TesseractBootstrapperTests
{
    [Fact]
    public void IsLanguageDataAvailable_English_ReturnsTrue()
    {
        Assert.True(TesseractBootstrapper.IsLanguageDataAvailable("eng"));
    }

    [Fact]
    public void IsLanguageDataAvailable_UnknownLanguage_ReturnsFalse()
    {
        Assert.False(TesseractBootstrapper.IsLanguageDataAvailable("zzz_unknown_lang"));
    }

    [Fact]
    public void IsLanguageDataAvailable_TruncatedFile_ReturnsFalse()
    {
        var tessDir = Path.Combine(AppContext.BaseDirectory, "tesseract");
        Directory.CreateDirectory(tessDir);
        var targetFile = Path.Combine(tessDir, "corrupt_test.traineddata");

        try
        {
            File.WriteAllText(targetFile, "not a real traineddata file");
            Assert.False(TesseractBootstrapper.IsLanguageDataAvailable("corrupt_test"));
        }
        finally
        {
            try { File.Delete(targetFile); } catch { }
        }
    }

    [Fact]
    public async Task RepairLanguageData_English_ReextractsValidFile()
    {
        var tessDir = Path.Combine(AppContext.BaseDirectory, "tesseract");
        Directory.CreateDirectory(tessDir);
        var engFile = Path.Combine(tessDir, "eng.traineddata");

        Assert.True(File.Exists(engFile), "eng.traineddata should exist before test");
        var originalLength = new FileInfo(engFile).Length;
        Assert.True(originalLength >= 500_000, "eng.traineddata should be at least 500KB");

        await TesseractBootstrapper.RepairLanguageDataAsync("eng", CancellationToken.None);

        Assert.True(TesseractBootstrapper.IsLanguageDataAvailable("eng"));
        var repairedLength = new FileInfo(engFile).Length;
        Assert.Equal(originalLength, repairedLength);
    }

    [Fact]
    public void IsLanguageDataAvailable_EmptyFile_ReturnsFalse()
    {
        var tessDir = Path.Combine(AppContext.BaseDirectory, "tesseract");
        Directory.CreateDirectory(tessDir);
        var targetFile = Path.Combine(tessDir, "empty_test.traineddata");

        try
        {
            File.WriteAllBytes(targetFile, Array.Empty<byte>());
            Assert.False(TesseractBootstrapper.IsLanguageDataAvailable("empty_test"));
        }
        finally
        {
            try { File.Delete(targetFile); } catch { }
        }
    }
}
