using System.Drawing.Imaging;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class NativeTesseractEngineRealTests : IDisposable
{
    private static readonly string[] TessTestLines = ["Divine Orb", "Exalted Orb"];
    private NativeTesseractEngine? _engine;

    public void Dispose()
    {
        _engine?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CreateEngine_WithValidLanguage_Succeeds()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return; // traineddata not available in this environment

        _engine = new NativeTesseractEngine(tessDataPath, "eng");
        Assert.NotNull(_engine);
    }

    [Fact]
    public void Recognize_KnownText_ReturnsExpectedOutput()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return;

        _engine = new NativeTesseractEngine(tessDataPath, "eng");
        _engine.SetPageSegMode(3); // PSM_AUTO

        using var bitmap = CreateTextBitmap("Chaos Orb", 24, Color.White, Color.Black);
        var text = _engine.Recognize(bitmap, out _, upscaleFactor: 1);

        Assert.Contains("Chaos", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognize_MultipleLines_DetectsBoth()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return;

        _engine = new NativeTesseractEngine(tessDataPath, "eng");
        _engine.SetPageSegMode(3);

        using var bitmap = CreateMultilineBitmap(TessTestLines, 20, Color.White, Color.Black);
        var text = _engine.Recognize(bitmap, out _, upscaleFactor: 1);

        Assert.Contains("Divine", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Exalted", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognize_WhiteOnDark_UpscaledForAccuracy()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return;

        _engine = new NativeTesseractEngine(tessDataPath, "eng");
        _engine.SetPageSegMode(3);

        using var bitmap = CreateTextBitmap("Vaal Orb", 32, Color.White, Color.FromArgb(20, 20, 20));
        var text = _engine.Recognize(bitmap, out _, upscaleFactor: 2);

        Assert.NotNull(text);
        Assert.NotEmpty(text);
    }

    [Fact]
    public void Recognize_EmptyBitmap_DoesNotThrow()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return;

        _engine = new NativeTesseractEngine(tessDataPath, "eng");
        _engine.SetPageSegMode(3);

        using var bitmap = new Bitmap(100, 30, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);

        var text = _engine.Recognize(bitmap, out _, upscaleFactor: 1);
        Assert.NotNull(text);
    }

    [Fact]
    public void EngineDispose_CanRecreate()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return;

        _engine = new NativeTesseractEngine(tessDataPath, "eng");
        _engine.Dispose();

        _engine = new NativeTesseractEngine(tessDataPath, "eng");
        Assert.NotNull(_engine);
    }

    [Fact]
    public void EngineDispose_DoubleDispose_DoesNotThrow()
    {
        var tessDataPath = TesseractBootstrapper.ResolveTessDataPath();
        if (string.IsNullOrWhiteSpace(tessDataPath))
            return;

        _engine = new NativeTesseractEngine(tessDataPath, "eng");
        _engine.Dispose();
        _engine.Dispose(); // Double dispose should be safe
    }

    [Fact]
    public void Constructor_NullDataPath_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new NativeTesseractEngine(null!, "eng"));
    }

    private static Bitmap CreateTextBitmap(string text, int fontSize, Color textColor, Color bgColor)
    {
        using var font = new Font("Arial", fontSize, FontStyle.Regular);
        using var temp = new Bitmap(1, 1);
        using var tempG = Graphics.FromImage(temp);
        var size = tempG.MeasureString(text, font);

        var bitmap = new Bitmap((int)Math.Ceiling(size.Width) + 20, (int)Math.Ceiling(size.Height) + 10, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(bgColor);
        using var brush = new SolidBrush(textColor);
        g.DrawString(text, font, brush, 5, 5);

        return bitmap;
    }

    private static Bitmap CreateMultilineBitmap(string[] lines, int fontSize, Color textColor, Color bgColor)
    {
        using var font = new Font("Arial", fontSize, FontStyle.Regular);
        var lineHeight = fontSize + 6;
        var maxWidth = 0;
        using var temp = new Bitmap(1, 1);
        using var tempG = Graphics.FromImage(temp);
        foreach (var line in lines)
        {
            var sz = tempG.MeasureString(line, font);
            if (sz.Width > maxWidth) maxWidth = (int)Math.Ceiling(sz.Width);
        }

        var bitmap = new Bitmap(maxWidth + 20, lineHeight * lines.Length + 10, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(bgColor);
        using var brush = new SolidBrush(textColor);
        for (var i = 0; i < lines.Length; i++)
            g.DrawString(lines[i], font, brush, 5, 5 + i * lineHeight);

        return bitmap;
    }
}
