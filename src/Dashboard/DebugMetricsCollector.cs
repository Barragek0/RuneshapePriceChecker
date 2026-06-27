using System.Collections.Concurrent;
using System.IO;

namespace RuneshapePriceChecker.App.Dashboard;

public sealed class DebugMetricsCollector
{
    private readonly double[] _durationBuffer = new double[MaxSamples];
    private int _durationIndex;
    private int _durationCount;
    private readonly double[] _uncachedDurationBuffer = new double[MaxSamples];
    private int _uncachedIndex;
    private int _uncachedCount;
    private readonly double[] _cachedDurationBuffer = new double[MaxSamples];
    private int _cachedIndex;
    private int _cachedCount;
    private struct SlotBuffer
    {
        public double[] Buffer;
        public int Index;
        public int Count;
    }

    private readonly SlotBuffer[] _slotBuffers;
    private long _totalCycles;
    private long _fullOcrCycles;
    private long _cacheHits;
    private DateTime _lastCpuCheck = DateTime.UtcNow;
    private TimeSpan _lastCpuTime;
    private double _cpuPercent;
    private long _lastCycleCount;
    private double _accumulatedScanMs;
    private double _scansPerSec;
    private double _scanCpuPercent;
    private long _lastWriteBytes;
    private double _diskWriteBytesPerSec;
    private volatile bool _isPoe2Foreground;
    private volatile bool _interfaceDetected;
    private volatile int _itemsDetected;
    private volatile int _cacheSize;
    private volatile int _anchorCheckPasses;
    private volatile int _anchorCheckFails;

    public bool IsPoe2Foreground { get => _isPoe2Foreground; set => _isPoe2Foreground = value; }
    public bool InterfaceDetected { get => _interfaceDetected; set => _interfaceDetected = value; }
    public int ItemsDetected { get => _itemsDetected; set => _itemsDetected = value; }
    public int CacheSize { get => _cacheSize; set => _cacheSize = value; }
    public int AnchorCheckPasses { get => _anchorCheckPasses; set => _anchorCheckPasses = value; }
    public int AnchorCheckFails { get => _anchorCheckFails; set => _anchorCheckFails = value; }
    public bool DebugOverlayActive { get; set; }

    public string CaptureMethod { get; set; } = "";
    public string OcrBackend { get; set; } = "";
    public ConcurrentDictionary<string, byte> FailedCaptureModes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string PricingSource { get; set; } = "";
    public string CurrentLeague { get; set; } = "";
    public string RegionInfo { get; set; } = "";
    public string Language { get; set; } = "";
    public string OcrEngineMode { get; set; } = "";

    public DateTime StartTimeUtc { get; } = DateTime.UtcNow;
    public long TotalCycles => Interlocked.Read(ref _totalCycles);
    public long FullOcrCycles => Interlocked.Read(ref _fullOcrCycles);
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public double CacheHitRate
    {
        get
        {
            lock (_lock)
            {
                var count = _cacheHitCount;
                if (count == 0) return 0;
                var hits = 0;
                for (var i = 0; i < count; i++)
                {
                    if (_cacheHitBuffer[i])
                        hits++;
                }
                return (double)hits / count * 100;
            }
        }
    }
    private readonly bool[] _cacheHitBuffer = new bool[MaxSamples];
    private int _cacheHitIndex;
    private int _cacheHitCount;

    private const int MaxSamples = 40;
    private readonly object _lock = new();
    internal static class SlotIndex
    {
        public const int Total = 0;
        public const int Capture = 1;
        public const int AnchorCheck = 2;
        public const int FrameHash = 3;
        public const int KeepBlack = 4;
        public const int Preprocess = 5;
        public const int Upscale = 6;
        public const int PixEncode = 7;
        public const int Recognize = 8;
        public const int TsvParse = 9;
        public const int PostProcess = 10;
        public const int CacheHit = 11;
        public const int Count = 12;
    }

    public DebugMetricsCollector()
    {
        _slotBuffers = new SlotBuffer[SlotIndex.Count];
        for (var i = 0; i < _slotBuffers.Length; i++)
            _slotBuffers[i] = new SlotBuffer { Buffer = new double[MaxSamples] };
    }
    public void RecordCycle(double totalDurationMs, bool fromCache, bool isFullOcr)
    {
        _ = Interlocked.Increment(ref _totalCycles);
        if (fromCache)
            _ = Interlocked.Increment(ref _cacheHits);
        if (isFullOcr)
            _ = Interlocked.Increment(ref _fullOcrCycles);

        lock (_lock)
        {
            _durationBuffer[_durationIndex] = totalDurationMs;
            _durationIndex = (_durationIndex + 1) % MaxSamples;
            if (_durationCount < MaxSamples)
                _durationCount++;
            _accumulatedScanMs += totalDurationMs;

            // Rolling cache-hit window (same size as duration windows)
            _cacheHitBuffer[_cacheHitIndex] = fromCache;
            _cacheHitIndex = (_cacheHitIndex + 1) % MaxSamples;
            if (_cacheHitCount < MaxSamples)
                _cacheHitCount++;

            // Separate duration tracking per cache status
            if (fromCache)
            {
                _cachedDurationBuffer[_cachedIndex] = totalDurationMs;
                _cachedIndex = (_cachedIndex + 1) % MaxSamples;
                if (_cachedCount < MaxSamples)
                    _cachedCount++;
            }
            else
            {
                _uncachedDurationBuffer[_uncachedIndex] = totalDurationMs;
                _uncachedIndex = (_uncachedIndex + 1) % MaxSamples;
                if (_uncachedCount < MaxSamples)
                    _uncachedCount++;
            }
        }
    }
    public void RecordSlotDuration(int slotIndex, double durationMs)
    {
        if (slotIndex < 0 || slotIndex >= _slotBuffers.Length) return;

        lock (_lock)
        {
            var buf = _slotBuffers[slotIndex];
            buf.Buffer[buf.Index] = durationMs;
            buf.Index = (buf.Index + 1) % MaxSamples;
            if (buf.Count < MaxSamples)
                buf.Count++;
            _slotBuffers[slotIndex] = buf;
        }
    }
    public DebugMetricsSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var avg = 0d;
            var max = 0d;
            var min = double.MaxValue;
            var count = _durationCount;

            if (count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var d = _durationBuffer[i];
                    avg += d;
                    if (d > max) max = d;
                    if (d < min) min = d;
                }
                avg /= count;
            }
            else
            {
                min = 0;
            }

            // Separate cached / uncached averages
            var uncachedAvg = 0d;
            if (_uncachedCount > 0)
            {
                for (var i = 0; i < _uncachedCount; i++)
                    uncachedAvg += _uncachedDurationBuffer[i];
                uncachedAvg /= _uncachedCount;
            }

            var cachedAvg = 0d;
            if (_cachedCount > 0)
            {
                for (var i = 0; i < _cachedCount; i++)
                    cachedAvg += _cachedDurationBuffer[i];
                cachedAvg /= _cachedCount;
            }

            var slotAvgs = new double[SlotIndex.Count];
            for (var i = 0; i < _slotBuffers.Length; i++)
            {
                var buf = _slotBuffers[i];
                if (buf.Count > 0)
                {
                    var sum = 0d;
                    for (var j = 0; j < buf.Count; j++)
                        sum += buf.Buffer[j];
                    slotAvgs[i] = sum / buf.Count;
                }
            }

            var total = TotalCycles;
            var hits = CacheHits;

            // Rolling cache-hit rate over MaxSamples window
            var rollingHitCount = 0;
            for (var ri = 0; ri < _cacheHitCount; ri++)
            {
                if (_cacheHitBuffer[ri])
                    rollingHitCount++;
            }
            var rollingHitRate = _cacheHitCount > 0
                ? Math.Round((double)rollingHitCount / _cacheHitCount * 100, 1)
                : 0;

            // Measure CPU usage, wall-clock scan rate, and disk I/O
            var now = DateTime.UtcNow;
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var cpuTime = proc.TotalProcessorTime;
            var elapsed = (now - _lastCpuCheck).TotalSeconds;
            if (elapsed >= 0.5)
            {
                var cpuDelta = (cpuTime - _lastCpuTime).TotalSeconds;
                _cpuPercent = cpuDelta / (elapsed * Environment.ProcessorCount) * 100;
                var cyclesSinceCpuCheck = total - _lastCycleCount;
                _scansPerSec = cyclesSinceCpuCheck / elapsed;
                _lastCpuCheck = now;
                _lastCpuTime = cpuTime;

                // Scan CPU as a % of total CPU capacity
                var wallMs = elapsed * 1000;
                _scanCpuPercent = _accumulatedScanMs / (wallMs * Environment.ProcessorCount) * 100;
                _lastCycleCount = total;
                _accumulatedScanMs = 0;

                // Disk I/O: track write bytes via log file size growth
                try
                {
                    var files = Directory.GetFiles(
                        Path.Combine(AppContext.BaseDirectory, "logs"), "*-log.txt");
                    if (files.Length > 0)
                    {
                        var latest = files.OrderByDescending(f => f).First();
                        var len = new FileInfo(latest).Length;
                        _diskWriteBytesPerSec = (len - _lastWriteBytes) / elapsed;
                        _lastWriteBytes = len;
                    }
                }
                catch { }

            }

            var memBytes = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;

            return new DebugMetricsSnapshot
            {
                AverageScanDurationMs = Math.Round(avg, 1),
                AverageUncachedDurationMs = Math.Round(uncachedAvg, 1),
                AverageCachedDurationMs = Math.Round(cachedAvg, 1),
                AverageOverheadMs = _scansPerSec > 0
                    ? Math.Round(Math.Max(0, (1000.0 / _scansPerSec) - avg), 1)
                    : 0,
                ScansPerSecond = Math.Round(_scansPerSec, 1),
                TotalScans = total,
                FullOcrScans = FullOcrCycles,
                CacheHits = hits,
                CacheHitRate = rollingHitRate,
                SlotAveragesMs = slotAvgs,
                IsPoe2Foreground = _isPoe2Foreground,
                InterfaceDetected = _interfaceDetected,
                CaptureMethod = CaptureMethod,
                OcrBackend = OcrBackend,
                PricingSource = PricingSource,
                CurrentLeague = CurrentLeague,
                ItemsDetected = _itemsDetected,
                CacheSize = _cacheSize,
                RegionInfo = RegionInfo,
                Language = Language,
                OcrEngineMode = OcrEngineMode,
                AnchorCheckPasses = _anchorCheckPasses,
                AnchorCheckFails = _anchorCheckFails,
                DebugOverlayActive = DebugOverlayActive,
                Uptime = DateTime.UtcNow - StartTimeUtc,
                CpuPercent = Math.Round(_cpuPercent, 1),
                ScanCpuPercent = Math.Round(_scanCpuPercent, 1),
                MemoryMb = memBytes / (1024 * 1024),
                DiskReadBytesPerSec = 0,
                DiskWriteBytesPerSec = Math.Round(_diskWriteBytesPerSec, 0),
            };
        }
    }
}

public sealed class DebugMetricsSnapshot
{
    public double AverageScanDurationMs { get; init; }
    public double AverageUncachedDurationMs { get; init; }
    public double AverageCachedDurationMs { get; init; }
    public double AverageOverheadMs { get; init; }
    public double ScansPerSecond { get; init; }
    public long TotalScans { get; init; }
    public long FullOcrScans { get; init; }
    public long CacheHits { get; init; }
    public double CacheHitRate { get; init; }
    public double[] SlotAveragesMs { get; init; } = [];
    public bool IsPoe2Foreground { get; init; }
    public bool InterfaceDetected { get; init; }
    public string CaptureMethod { get; init; } = "";
    public string OcrBackend { get; init; } = "";
    public string PricingSource { get; init; } = "";
    public string CurrentLeague { get; init; } = "";
    public int ItemsDetected { get; init; }
    public int CacheSize { get; init; }
    public string RegionInfo { get; init; } = "";
    public string Language { get; init; } = "";
    public string OcrEngineMode { get; init; } = "";
    public int AnchorCheckPasses { get; init; }
    public int AnchorCheckFails { get; init; }
    public bool DebugOverlayActive { get; init; }
    public TimeSpan Uptime { get; init; }
    public double CpuPercent { get; init; }
    public double ScanCpuPercent { get; init; }
    public long MemoryMb { get; init; }
    public double DiskReadBytesPerSec { get; init; }
    public double DiskWriteBytesPerSec { get; init; }
}
