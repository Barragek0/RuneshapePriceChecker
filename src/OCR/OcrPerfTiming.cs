using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace RuneshapePriceChecker.OCR;

internal sealed class OcrPerfTiming
{
    private readonly long[] _accum = new long[(int)Slot.Count];
    private readonly int[] _counts = new int[(int)Slot.Count];
    private readonly double[] _cycleSlotMs = new double[(int)Slot.Count];

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimedRegion Measure(Slot slot)
    {
        var start = Stopwatch.GetTimestamp();
        return new TimedRegion(this, slot, start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long RecordStart(Slot _) => Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordEnd(Slot slot, long startTicks)
    {
        var idx = (int)slot;
        var delta = Stopwatch.GetTimestamp() - startTicks;
        _accum[idx] += delta;
        _counts[idx]++;
        _cycleSlotMs[idx] += delta * 1000.0 / Stopwatch.Frequency;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record(Slot slot, long startTicks) => RecordEnd(slot, startTicks);
    public double GetSlotAverageMs(Slot slot)
    {
        var idx = (int)slot;
        var count = _counts[idx];
        if (count == 0) return 0;
        return _accum[idx] * 1000.0 / Stopwatch.Frequency / count;
    }
    public void ResetCycleSlotMs() => Array.Clear(_cycleSlotMs);
    public double[] GetCycleSlotMs() => _cycleSlotMs;

    public string GetAndResetReport()
    {
        var sb = new System.Text.StringBuilder(256);
        _ = sb.Append(CultureInfo.InvariantCulture, $"OCR perf (avg ms): ");
        var freq = Stopwatch.Frequency;
        for (var i = 0; i < (int)Slot.Count; i++)
        {
            if (_counts[i] == 0) continue;
            var avgMs = _accum[i] * 1000L / freq / _counts[i];
            _ = sb.Append(CultureInfo.InvariantCulture, $"{(Slot)i}={avgMs}ms ");
            _accum[i] = 0;
            _counts[i] = 0;
        }
        return sb.ToString();
    }

    internal readonly struct TimedRegion(OcrPerfTiming owner, Slot slot, long startTicks) : IDisposable
    {
        public void Dispose() => owner.Record(slot, startTicks);
    }
}
