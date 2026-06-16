using System.Diagnostics;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Startup;
using Xunit;
using Xunit.Abstractions;

namespace RuneshapePriceChecker.Tests.OCR;

public sealed class OcrBackendBenchmark(ITestOutputHelper output)
{
    private const int WarmupRounds = 5;
    private const int MeasureRounds = 20;
    private const string BenchmarkText = "Exalted Orb\nDivine Orb\nChaos Orb\nMirror of Kalandra\nVaal Orb\nAnnulment Orb\nRegal Orb\nAlchemy Orb";

    [Fact]
    public void CompareWindowsVsTesseractPerformance()
    {
        using var benchmarkBitmap = CreateBenchmarkBitmap();
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        output.WriteLine($"TessData path: {(string.IsNullOrEmpty(tessDataPath) ? "(empty)" : tessDataPath)}");

        // Warmup Tesseract
        using var tesseractEngine = new NativeTesseractEngine(tessDataPath, "eng", 2);
        tesseractEngine.SetPageSegMode(4);
        for (var i = 0; i < WarmupRounds; i++)
            tesseractEngine.Recognize(benchmarkBitmap, out _, 1);

        // Measure Tesseract
        var tesseractTimes = new long[MeasureRounds];
        for (var i = 0; i < MeasureRounds; i++)
        {
            var sw = Stopwatch.StartNew();
            tesseractEngine.Recognize(benchmarkBitmap, out _, 1);
            tesseractTimes[i] = sw.ElapsedTicks;
        }

        // Warmup Windows OCR
        using var windowsEngine = new WindowsOcrEngine();
        for (var i = 0; i < WarmupRounds; i++)
            windowsEngine.Recognize(benchmarkBitmap, out _, 1);

        // Measure Windows OCR
        var windowsTimes = new long[MeasureRounds];
        for (var i = 0; i < MeasureRounds; i++)
        {
            var sw = Stopwatch.StartNew();
            windowsEngine.Recognize(benchmarkBitmap, out _, 1);
            windowsTimes[i] = sw.ElapsedTicks;
        }

        var tAvgMs = tesseractTimes.Average() / (double)Stopwatch.Frequency * 1000.0;
        var wAvgMs = windowsTimes.Average() / (double)Stopwatch.Frequency * 1000.0;
        var ratio = tAvgMs / wAvgMs;

        output.WriteLine($"=== OCR Backend Benchmark ({MeasureRounds} rounds after {WarmupRounds} warmup) ===");
        output.WriteLine($"Tesseract avg: {tAvgMs:F2} ms");
        output.WriteLine($"Windows OCR avg: {wAvgMs:F2} ms");
        output.WriteLine($"Speedup: {ratio:F1}x {(ratio > 1.0 ? "(Windows faster)" : "(Tesseract faster)")}");
        output.WriteLine("");
        output.WriteLine(ratio > 1.0
            ? "VERDICT: Windows.Media.Ocr is faster — switch default."
            : "VERDICT: Tesseract is faster — keep as default.");

        Assert.True(true);
    }

    private static Bitmap CreateBenchmarkBitmap()
    {
        var bmp = new Bitmap(400, 120);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using var font = new Font("Consolas", 14, FontStyle.Regular);
        using var brush = new SolidBrush(Color.Black);
        g.DrawString(BenchmarkText, font, brush, 10, 10);
        return bmp;
    }
}
