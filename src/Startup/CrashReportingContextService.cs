using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.OCR;

namespace RuneshapePriceChecker.Startup;

internal sealed class CrashReportingContextService(
    SentryCrashReporter reporter,
    IOptionsMonitor<AppOptions> appOptions,
    IOptionsMonitor<OcrOptions> ocrOptions,
    IOptionsMonitor<PricingCacheOptions> pricingOptions) : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = SentryPaths.ContextPath;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();
    private bool _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Refresh();
        AddSubscription(appOptions.OnChange(_ => Refresh()));
        AddSubscription(ocrOptions.OnChange(_ => Refresh()));
        AddSubscription(pricingOptions.OnChange(_ => Refresh()));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var app = appOptions.CurrentValue;
                var ocr = ocrOptions.CurrentValue;
                var pricing = pricingOptions.CurrentValue;
                var context = new
                {
                    schemaVersion = 1,
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    appVersion = typeof(CrashReportingContextService).Assembly.GetName().Version?.ToString() ?? "unknown",
                    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    osArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    processorCount = Environment.ProcessorCount,
                    is64BitProcess = Environment.Is64BitProcess,
                    workingSetMiB = Environment.WorkingSet / 1024 / 1024,
                    uptimeMilliseconds = Environment.TickCount64,
                    logLevel = app.LogLevel.ToString(),
                    autoRestartOnCrash = app.AutoRestartOnCrash,
                    ocrBackend = ocr.OcrBackend,
                    ocrLanguage = ocr.Language,
                    ocrCaptureMode = ocr.CaptureMode,
                    ocrPreprocessing = ocr.EnableImagePreprocessing,
                    ocrEngineMode = ocr.OcrEngineMode,
                    ocrScanIntervalMilliseconds = ocr.ScanIntervalMs,
                    pricingSource = pricing.PricingSource,
                    league = pricing.League
                };

                var directory = Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                var temp = _path + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    JsonSerializer.Serialize(stream, context, JsonOptions);
                    stream.Flush(true);
                }
                File.Move(temp, _path, true);

                reporter.SetTag("ocr.backend", ocr.OcrBackend);
                reporter.SetTag("ocr.language", ocr.Language);
                reporter.SetTag("ocr.capture_mode", ocr.CaptureMode);
            }
            catch { }
        }
    }

    private void AddSubscription(IDisposable? subscription)
    {
        if (subscription is not null)
            _subscriptions.Add(subscription);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
