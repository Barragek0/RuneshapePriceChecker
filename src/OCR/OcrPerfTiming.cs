using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace RuneshapePriceChecker.OCR;

internal sealed class OcrPerfTiming
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly long[] _accum = new long[(int)Slot.Count];
    private readonly int[] _counts = new int[(int)Slot.Count];
    private int _cycleCount;
    private const int LogInterval = 20;

    internal enum Slot
    {
        Total,
        Capture,
        AnchorCheck,
        FrameHash,
        KeepBlack,
        Preprocess,
        Upscale,
        PixEncode,
        Recognize,
        TsvParse,
        PostProcess,
        CacheHit,
        Count
    }

    private int _fullOcrCycleCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimedRegion Measure(Slot slot)
    {
        var start = _sw.ElapsedTicks;
        return new TimedRegion(this, slot, start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long RecordStart(Slot slot) => _sw.ElapsedTicks;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordEnd(Slot slot, long startTicks)
    {
        var idx = (int)slot;
        _accum[idx] += _sw.ElapsedTicks - startTicks;
        _counts[idx]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record(Slot slot, long startTicks)
    {
        RecordEnd(slot, startTicks);
    }

    /// <summary>Call at end of every cycle (cache hit or full OCR).</summary>
    public bool ShouldLog()
    {
        _cycleCount++;
        return _cycleCount % LogInterval == 0;
    }

    /// <summary>Only logs when full OCR cycles have run (not just cache hits).</summary>
    public bool ShouldLogFullOcr()
    {
        _fullOcrCycleCount++;
        return _fullOcrCycleCount % LogInterval == 0;
    }

    public string GetAndResetReport()
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(CultureInfo.InvariantCulture, $"OCR perf (avg ms): ");
        var freq = Stopwatch.Frequency;
        for (var i = 0; i < (int)Slot.Count; i++)
        {
            if (_counts[i] == 0) continue;
            var avgMs = _accum[i] * 1000L / freq / _counts[i];
            sb.Append(CultureInfo.InvariantCulture, $"{(Slot)i}={avgMs}ms ");
            _accum[i] = 0;
            _counts[i] = 0;
        }
        _cycleCount = 0;
        return sb.ToString();
    }

    internal readonly struct TimedRegion(OcrPerfTiming owner, Slot slot, long startTicks) : IDisposable
    {
        public void Dispose() => owner.Record(slot, startTicks);
    }
}
