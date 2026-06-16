using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RuneshapePriceChecker.OCR;

internal sealed partial class NativeTesseractEngine : IDisposable
{
    private IntPtr _handle;

    public NativeTesseractEngine(string tesseractDataPath, string language, int engineMode = 2)
    {
        _handle = NativeMethods.TessBaseAPICreate();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create Tesseract engine handle.");

        _ = NativeMethods.TessBaseAPISetVariable(_handle, "tessedit_ocr_engine_mode", engineMode.ToString(CultureInfo.InvariantCulture));
        _ = NativeMethods.TessBaseAPISetVariable(_handle, "preserve_interword_spaces", "1");
        _ = NativeMethods.TessBaseAPISetVariable(_handle, "debug_file", "nul");
        _ = NativeMethods.TessBaseAPISetVariable(_handle, "load_system_dawg", "false");
        _ = NativeMethods.TessBaseAPISetVariable(_handle, "load_freq_dawg", "false");
        _ = NativeMethods.TessBaseAPISetVariable(_handle, "classify_enable_learning", "0");

        var result = NativeMethods.TessBaseAPIInit3(_handle, tesseractDataPath, language);
        if (result != 0)
            throw new InvalidOperationException($"Tesseract init failed (code {result}). datapath='{tesseractDataPath}' language='{language}'");
    }

    public void SetPageSegMode(int mode)
    {
        NativeMethods.TessBaseAPISetPageSegMode(_handle, mode);
    }

    /// <summary>Recognize a single line of text (PSM 7). Returns text only — no TSV parsing needed.</summary>
    public string RecognizeSingleLine(Bitmap rowBitmap)
    {
        var pix = IntPtr.Zero;
        try
        {
            pix = CreatePixFromBitmap(rowBitmap);
            NativeMethods.TessBaseAPISetPageSegMode(_handle, 7); // PSM_SINGLE_LINE
            NativeMethods.TessBaseAPISetImage2(_handle, pix);
            _ = NativeMethods.TessBaseAPIRecognize(_handle, IntPtr.Zero);

            var textPtr = NativeMethods.TessBaseAPIGetUTF8Text(_handle);
            var text = textPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(textPtr) ?? string.Empty : string.Empty;
            if (textPtr != IntPtr.Zero)
                NativeMethods.TessDeleteText(textPtr);

            return text.TrimEnd('\r', '\n');
        }
        finally
        {
            if (pix != IntPtr.Zero)
                NativeMethods.pixDestroy(ref pix);
        }
    }

    public string Recognize(Bitmap bitmap, out int[] wordYPositions, int upscaleFactor, OcrPerfTiming? perf = null)
    {
        var pix = IntPtr.Zero;
        try
        {
            IntPtr localPix;
            using (perf?.Measure(OcrPerfTiming.Slot.PixEncode))
                localPix = CreatePixFromBitmap(bitmap);
            pix = localPix;

            using (perf?.Measure(OcrPerfTiming.Slot.Recognize))
            {
                NativeMethods.TessBaseAPISetImage2(_handle, pix);
                _ = NativeMethods.TessBaseAPIRecognize(_handle, IntPtr.Zero);
            }

            var textPtr = NativeMethods.TessBaseAPIGetUTF8Text(_handle);
            var text = textPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(textPtr) ?? string.Empty : string.Empty;
            if (textPtr != IntPtr.Zero)
                NativeMethods.TessDeleteText(textPtr);

            using (perf?.Measure(OcrPerfTiming.Slot.TsvParse))
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
        var tsvPtr = NativeMethods.TessBaseAPIGetTSVText(_handle, 0);
        if (tsvPtr == IntPtr.Zero)
            return [];

        var tsv = Marshal.PtrToStringAnsi(tsvPtr) ?? string.Empty;
        NativeMethods.TessDeleteText(tsvPtr);

        return ParseWordYPositions(tsv, upscaleFactor);
    }

    private static int[] ParseWordYPositions(ReadOnlySpan<char> tsv, int upscaleFactor)
    {
        // Skip past header line
        var idx = tsv.IndexOf('\n');
        if (idx < 0 || idx >= tsv.Length - 1) return [];
        var pos = idx + 1;

        // Pre-count word entries to avoid List<T> reallocation
        var wordCount = 0;
        for (var i = pos; i < tsv.Length; i++)
            if (tsv[i] == '\n' && i + 1 < tsv.Length && tsv[i + 1] == '4')
                wordCount++;
        if (wordCount == 0) return [];

        var positions = new int[wordCount];
        var count = 0;
        while (pos < tsv.Length)
        {
            var end = pos;
            while (end < tsv.Length && tsv[end] != '\n') end++;

            var line = tsv[pos..end];
            if (!line.IsEmpty && line[0] == '4')
            {
                var col = 0;
                var fieldStart = 0;
                for (var i = 0; i <= line.Length; i++)
                {
                    if (i != line.Length && line[i] != '\t') continue;
                    if (col == 7 && int.TryParse(line[fieldStart..i], out var top))
                    {
                        positions[count++] = (top - 12) / upscaleFactor;
                        break;
                    }
                    col++;
                    fieldStart = i + 1;
                    if (col > 7) break;
                }
            }

            pos = end + 1;
        }

        if (count == positions.Length) return positions;
        return positions.AsSpan(0, count).ToArray();
    }

    private static IntPtr CreatePixFromBitmap(Bitmap bitmap)
    {
        // Write BMP bytes directly from LockBits data, bypassing GDI+ encoding.
        // BMP format: 14-byte file header + 40-byte DIB header + packed pixel rows.
        var width = bitmap.Width;
        var height = bitmap.Height;
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var rowBytes = width * 3;
            var padBytes = stride - rowBytes; // DWORD padding
            var pixelDataSize = stride * height;
            var fileSize = 54 + pixelDataSize;

            var bmpBytes = new byte[fileSize];

            // BITMAPFILEHEADER
            bmpBytes[0] = (byte)'B';
            bmpBytes[1] = (byte)'M';
            BitConverter.TryWriteBytes(bmpBytes.AsSpan(2), fileSize);
            BitConverter.TryWriteBytes(bmpBytes.AsSpan(10), 54);

            // BITMAPINFOHEADER
            BitConverter.TryWriteBytes(bmpBytes.AsSpan(14), 40);
            BitConverter.TryWriteBytes(bmpBytes.AsSpan(18), width);
            BitConverter.TryWriteBytes(bmpBytes.AsSpan(22), height);
            bmpBytes[26] = 1; // planes
            bmpBytes[28] = 24; // bpp
            BitConverter.TryWriteBytes(bmpBytes.AsSpan(34), pixelDataSize);

            // Copy rows from bottom to top (BMP order)
            var srcPtr = data.Scan0;
            for (var y = height - 1; y >= 0; y--)
            {
                var srcRow = IntPtr.Add(srcPtr, y * stride);
                var dstOffset = 54 + (height - 1 - y) * stride;
                Marshal.Copy(srcRow, bmpBytes, dstOffset, stride);
            }

            return NativeMethods.pixReadMem(bmpBytes, bmpBytes.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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
        public static extern int TessBaseAPIInit3(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string datapath, [MarshalAs(UnmanagedType.LPStr)] string language);

        [DllImport("tesseract50", CallingConvention = CallingConvention.Cdecl)]
        public static extern int TessBaseAPISetVariable(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);

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
