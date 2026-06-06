using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.OCR;

internal sealed class NativeTesseractEngine : IDisposable
{
    private IntPtr _handle;

    public NativeTesseractEngine(string tesseractDataPath, string language)
    {
        _handle = NativeMethods.TessBaseAPICreate();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create Tesseract engine handle.");

        var result = NativeMethods.TessBaseAPIInit3(_handle, tesseractDataPath, language);
        if (result != 0)
            throw new InvalidOperationException($"Tesseract init failed (code {result}). datapath='{tesseractDataPath}' language='{language}'");

        NativeMethods.TessBaseAPISetVariable(_handle, "preserve_interword_spaces", "1");
        NativeMethods.TessBaseAPISetVariable(_handle, "debug_file", "nul");
    }

    public void SetPageSegMode(int mode)
    {
        NativeMethods.TessBaseAPISetPageSegMode(_handle, mode);
    }

    public string Recognize(Bitmap bitmap, out int[] wordYPositions, int upscaleFactor)
    {
        var pix = IntPtr.Zero;
        try
        {
            pix = CreatePixFromBitmap(bitmap);
            NativeMethods.TessBaseAPISetImage2(_handle, pix);
            NativeMethods.TessBaseAPIRecognize(_handle, IntPtr.Zero);

            var textPtr = NativeMethods.TessBaseAPIGetUTF8Text(_handle);
            var text = textPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(textPtr) ?? string.Empty : string.Empty;
            if (textPtr != IntPtr.Zero)
                NativeMethods.TessDeleteText(textPtr);

            wordYPositions = ExtractWordYPositionsFromTsv(upscaleFactor);
            return text;
        }
        finally
        {
            if (pix != IntPtr.Zero)
                NativeMethods.pixDestroy(ref pix);
        }
    }

    private int[] ExtractWordYPositionsFromTsv(int upscaleFactor)
    {
        var positions = new List<int>();
        var tsvPtr = NativeMethods.TessBaseAPIGetTSVText(_handle, 0);
        if (tsvPtr == IntPtr.Zero)
            return positions.ToArray();

        var tsv = Marshal.PtrToStringAnsi(tsvPtr) ?? string.Empty;
        NativeMethods.TessDeleteText(tsvPtr);

        var lines = tsv.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var cols = lines[i].Split('\t');
            if (cols.Length < 12 || cols[0] != "4")
                continue;
            if (int.TryParse(cols[7], out var top) && int.TryParse(cols[9], out var h))
                positions.Add((top - 12) / upscaleFactor);
        }

        return positions.ToArray();
    }

    private static IntPtr CreatePixFromBitmap(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        var pngBytes = ms.ToArray();
        return NativeMethods.pixReadMem(pngBytes, pngBytes.Length);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.TessBaseAPIEnd(_handle);
            NativeMethods.TessBaseAPIDelete(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static class NativeMethods
    {
        [DllImport("leptonica-1.82.0", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr pixReadMem(byte[] data, int size);

        [DllImport("leptonica-1.82.0", CallingConvention = CallingConvention.Cdecl)]
        public static extern void pixDestroy(ref IntPtr pix);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr TessBaseAPICreate();

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern int TessBaseAPIInit3(IntPtr handle, string datapath, string language);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern int TessBaseAPISetVariable(IntPtr handle, string name, string value);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern void TessBaseAPISetPageSegMode(IntPtr handle, int mode);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern void TessBaseAPISetImage2(IntPtr handle, IntPtr pix);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern int TessBaseAPIRecognize(IntPtr handle, IntPtr monitor);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr TessBaseAPIGetUTF8Text(IntPtr handle);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl, EntryPoint = "TessBaseAPIGetTsvText")]
        public static extern IntPtr TessBaseAPIGetTSVText(IntPtr handle, int pageNumber);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern void TessDeleteText(IntPtr text);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern void TessBaseAPIEnd(IntPtr handle);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern void TessBaseAPIDelete(IntPtr handle);
    }
}
