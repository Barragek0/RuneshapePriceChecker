using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// CA1416: all callers guard with OS build >= 17763
#pragma warning disable CA1416

namespace RuneshapePriceChecker.OCR;

internal sealed class WindowsOcrEngine : IDisposable
{
    private readonly OcrEngine _engine;

    public WindowsOcrEngine()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR engine not available on this system.");
    }

    public string Recognize(Bitmap bitmap, out int[] wordYPositions, int upscaleFactor, OcrPerfTiming? perf = null)
    {
        var sw = perf?.RecordStart(OcrPerfTiming.Slot.Recognize);

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
        var stream = ms.ToArray().AsBuffer().AsStream().AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(stream).GetAwaiter().GetResult();
        using var softwareBitmap = decoder.GetSoftwareBitmapAsync().GetAwaiter().GetResult();

        var result = _engine.RecognizeAsync(softwareBitmap).GetAwaiter().GetResult();

        var positions = new List<int>(result.Lines.Count);
        var lineTexts = new string[result.Lines.Count];
        for (var i = 0; i < result.Lines.Count; i++)
        {
            var line = result.Lines[i];
            var words = new string[line.Words.Count];
            var ySum = 0.0;
            var hSum = 0.0;
            for (var w = 0; w < line.Words.Count; w++)
            {
                words[w] = line.Words[w].Text;
                var r = line.Words[w].BoundingRect;
                ySum += r.Y;
                hSum += r.Height;
            }
            lineTexts[i] = string.Join(" ", words);

            var yTop = line.Words.Count > 0
                ? (int)((ySum / line.Words.Count - 6) / upscaleFactor)
                : 0;
            positions.Add(yTop);
        }

        wordYPositions = [.. positions];
        if (perf is not null && sw.HasValue)
            perf.RecordEnd(OcrPerfTiming.Slot.Recognize, sw.Value);
        return string.Join("\n", lineTexts);
    }

    public void Dispose() { }
}
#pragma warning restore CA1416
