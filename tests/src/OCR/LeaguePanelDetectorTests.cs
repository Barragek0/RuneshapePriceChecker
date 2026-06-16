using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RuneshapePriceChecker.OCR;
using Xunit;

namespace RuneshapePriceChecker.Tests.OCR;

public class ListDetectorTests
{
    private static Bitmap CreateBrightBitmap()
    {
        const int w = 200, h = 100;
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var stride = Math.Abs(data.Stride);
        var bytes = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int rowOff = y * stride;
            for (int x = 0; x < w; x++)
            {
                int idx = rowOff + x * 3;
                if (x % 4 == 0 && y % 4 == 0)
                {
                    bytes[idx] = 5; bytes[idx + 1] = 5; bytes[idx + 2] = 5;
                }
                else
                {
                    bytes[idx] = 255; bytes[idx + 1] = 255; bytes[idx + 2] = 255;
                }
            }
        }
        Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    private static Bitmap CreateDarkBitmap()
    {
        const int w = 200, h = 100;
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var bytes = new byte[Math.Abs(data.Stride) * h];
        Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    private static Bitmap SharedBright => field ??= CreateBrightBitmap();
    private static Bitmap SharedDark => field ??= CreateDarkBitmap();

    [Fact]
    public void Update_InitialState_IsNotOpen()
    {
        Assert.False(new LeaguePanelDetector().IsOpen);
    }

    [Fact]
    public void Update_FirstBrightFrame_NotYetOpen()
    {
        var d = new LeaguePanelDetector();
        Assert.False(d.Update(SharedBright, out _));
        Assert.False(d.IsOpen);
    }

    [Fact]
    public void Update_ThreeBrightFrames_BecomesOpen()
    {
        var d = new LeaguePanelDetector();
        _ = d.Update(SharedBright, out _);
        _ = d.Update(SharedBright, out _);
        Assert.False(d.IsOpen); // 2 frames not enough
        Assert.True(d.Update(SharedBright, out _));
        Assert.True(d.IsOpen);
    }

    [Fact]
    public void Update_ThreeDarkFramesAfterOpen_BecomesClosed()
    {
        var d = new LeaguePanelDetector();
        _ = d.Update(SharedBright, out _);
        _ = d.Update(SharedBright, out _);
        _ = d.Update(SharedBright, out _);
        Assert.True(d.IsOpen);
        _ = d.Update(SharedDark, out _);
        Assert.True(d.IsOpen);
        _ = d.Update(SharedDark, out _);
        Assert.True(d.IsOpen);
        Assert.False(d.Update(SharedDark, out _));
        Assert.False(d.IsOpen);
    }

    [Fact]
    public void Update_DiagRecord_HasExpectedFields()
    {
        var d = new LeaguePanelDetector();
        _ = d.Update(SharedBright, out var diag);
        Assert.True(diag.TotalCount > 0);
        Assert.True(diag.AvgBrightness > 0);
        Assert.False(diag.PanelOpen);
    }

    [Fact]
    public void Update_ResetStreakOnOppositeSignal()
    {
        var d = new LeaguePanelDetector();
        _ = d.Update(SharedBright, out _);
        _ = d.Update(SharedDark, out _); // resets bright streak
        _ = d.Update(SharedBright, out _);
        _ = d.Update(SharedBright, out _);
        Assert.False(d.IsOpen); // only 2 consecutive bright after reset
        _ = d.Update(SharedBright, out _);
        Assert.True(d.IsOpen);
    }

    [Fact]
    public void Update_MultipleCycles_OpenCloseWorks()
    {
        var d = new LeaguePanelDetector();
        _ = d.Update(SharedBright, out _);
        _ = d.Update(SharedBright, out _);
        _ = d.Update(SharedBright, out _);
        Assert.True(d.IsOpen);
        _ = d.Update(SharedDark, out _);
        _ = d.Update(SharedDark, out _);
        _ = d.Update(SharedDark, out _);
        Assert.False(d.IsOpen);
        _ = d.Update(SharedBright, out _);
        _ = d.Update(SharedBright, out _);
        _ = d.Update(SharedBright, out _);
        Assert.True(d.IsOpen);
    }

    [Fact]
    public void Update_DarkBitmap_ProducesLowBrightness()
    {
        var d = new LeaguePanelDetector();
        _ = d.Update(SharedDark, out var diag);
        Assert.True(diag.AvgBrightness <= 140);
    }
}