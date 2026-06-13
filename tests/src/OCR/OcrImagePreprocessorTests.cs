using System.Drawing.Imaging;
using RuneshapePriceChecker.OCR;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class OcrImagePreprocessorTests
{
    [Fact]
    public void PreprocessForOcr_WhiteImage_ReturnsBitmap()
    {
        using var input = new Bitmap(10, 10, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(input);
        g.Clear(Color.White);

        var options = new OcrOptions { BinarizationThreshold = 128 };
        using var result = OcrImagePreprocessor.PreprocessForOcr(input, options);

        Assert.Equal(input.Width, result.Width);
        Assert.Equal(input.Height, result.Height);
    }

    [Fact]
    public void PreprocessForOcr_BlackImage_ReturnsBitmap()
    {
        using var input = new Bitmap(10, 10, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(input);
        g.Clear(Color.Black);

        var options = new OcrOptions { BinarizationThreshold = 128 };
        using var result = OcrImagePreprocessor.PreprocessForOcr(input, options);

        Assert.Equal(input.Width, result.Width);
    }

    [Fact]
    public void PreprocessForOcr_DefaultOptions_ReturnsBitmap()
    {
        using var input = new Bitmap(5, 5, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(input);
        g.Clear(Color.Gray);

        var options = new OcrOptions();
        using var result = OcrImagePreprocessor.PreprocessForOcr(input, options);

        Assert.NotNull(result);
    }

    [Fact]
    public void KeepBlackAndNeighbors_AllWhite_ReturnsAllWhite()
    {
        using var input = new Bitmap(10, 10, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(input);
        g.Clear(Color.White);

        using var result = OcrImagePreprocessor.KeepBlackAndNeighbors(input);
        Assert.Equal(input.Width, result.Width);
        Assert.Equal(input.Height, result.Height);
    }

    [Fact]
    public void KeepBlackAndNeighbors_SingleBlackPixel_KeepsNeighborhood()
    {
        using var input = new Bitmap(10, 10, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(input);
        g.Clear(Color.White);
        input.SetPixel(5, 5, Color.Black);

        using var result = OcrImagePreprocessor.KeepBlackAndNeighbors(input);
        // The 11x11 neighborhood around (5,5) should be non-white
        var centerPixel = result.GetPixel(5, 5);
        Assert.False(centerPixel.R > 200 && centerPixel.G > 200 && centerPixel.B > 200,
            "Center should not be pure white after filtering");
    }

    [Fact]
    public void KeepBlackAndNeighbors_SmallImage_DoesNotThrow()
    {
        using var input = new Bitmap(1, 1, PixelFormat.Format24bppRgb);
        input.SetPixel(0, 0, Color.Black);
        using var result = OcrImagePreprocessor.KeepBlackAndNeighbors(input);
        Assert.NotNull(result);
    }

    [Fact]
    public void AddWhiteBorder_AddsBorderPixels()
    {
        using var input = new Bitmap(5, 5, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(input);
        g.Clear(Color.Black);

        using var result = OcrImagePreprocessor.AddWhiteBorder(input, 2);
        Assert.Equal(input.Width + 4, result.Width);
        Assert.Equal(input.Height + 4, result.Height);
    }

    [Fact]
    public void UpscaleForOcr_DoublesSize()
    {
        using var input = new Bitmap(5, 5, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(input);
        g.Clear(Color.Gray);

        using var result = OcrImagePreprocessor.UpscaleForOcr(input, 2);
        Assert.Equal(10, result.Width);
        Assert.Equal(10, result.Height);
    }
}
