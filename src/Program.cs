using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using RuneshapePriceChecker.App;
using RuneshapePriceChecker.App.Dashboard;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Overlay;
using RuneshapePriceChecker.Pricing;
using RuneshapePriceChecker.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
{
    var ex = args.ExceptionObject as Exception;
    CrashLogger.WriteCrash($"AppDomain unhandled exception (terminating={args.IsTerminating})", ex);
    TrySaveCrashLog();
};

TaskScheduler.UnobservedTaskException += (sender, args) =>
{
    CrashLogger.WriteCaught("Unobserved task exception", args.Exception);
    args.SetObserved();
};

// Catch WinForms UI thread exceptions (OnPaint, event handlers, etc.) that WinForms
// swallows internally without routing to AppDomain.UnhandledException.
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += (sender, args) =>
{
    CrashLogger.WriteCrash($"WinForms thread exception ({args.Exception.GetType().Name})", args.Exception);
    TrySaveCrashLog();
};

if (args.Contains("--rpcservice"))
{
    RpcServiceRunner.Run();
    return;
}

var suppressWarning = false;
foreach (var a in args)
{
    if (a.StartsWith("--App:SuppressAlreadyRunningWarning=", StringComparison.OrdinalIgnoreCase))
    {
        suppressWarning = true;
        break;
    }
}
if (!suppressWarning)
{
    // Also check config file — post-update restarts won't have CLI args.
    try
    {
        var cfgPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
        if (File.Exists(cfgPath))
        {
            using var cfgDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
            if (cfgDoc.RootElement.TryGetProperty("App", out var app) &&
                app.TryGetProperty("SuppressAlreadyRunningWarning", out var sw) &&
                sw.ValueKind == JsonValueKind.True)
                suppressWarning = true;
        }
    }
    catch { }
}

Mutex? mutex = null;
var createdNew = false;
#pragma warning disable CA2000 // Mutex held for app lifetime; disposed at program exit
try
{
    mutex = new Mutex(true, @"Global\RuneshapePriceChecker_SingleInstance", out createdNew);
}
catch (AbandonedMutexException ex)
{
    mutex = ex.Mutex!;
    createdNew = false;
}

if (!createdNew && !suppressWarning)
{
    var result = MessageBox.Show(
        "RuneshapePriceChecker is already running.\n\nYes = close the old instance and start a new one\nNo = do nothing",
        "Already Running",
        MessageBoxButtons.YesNo,
        MessageBoxIcon.Question);

    if (result == DialogResult.Yes)
    {
        var selfName = Process.GetCurrentProcess().ProcessName;
        foreach (var proc in Process.GetProcessesByName(selfName))
        {
            if (proc.Id == Environment.ProcessId) continue;
            try { proc.Kill(); _ = proc.WaitForExit(3000); } catch { }
        }

        Thread.Sleep(500);

        mutex?.Dispose();
        mutex = new Mutex(true, @"Global\RuneshapePriceChecker_SingleInstance", out createdNew);
    }
    else
    {
        mutex?.Dispose();
        return;
    }
}
#pragma warning restore CA2000

var dashboardSink = new DashboardLogSink();
var dashboardLoggerProvider = new DashboardLoggerProvider(dashboardSink);
var metricsCollector = new DebugMetricsCollector();
var dashboardService = new DashboardService(dashboardSink, metricsCollector);
var hostCts = new CancellationTokenSource();
dashboardService.SetOnWindowClosed(hostCts.Cancel);
dashboardService.Start();

TryDeleteFile(Path.Combine(AppContext.BaseDirectory, "RuneshapePriceChecker.exe.old"));
TryDeleteFile(Path.Combine(AppContext.BaseDirectory, "Update.exe"));

var exeNewPath = Path.Combine(AppContext.BaseDirectory, "RuneshapePriceChecker.exe.new");
if (File.Exists(exeNewPath))
{
    var exePath = Path.Combine(AppContext.BaseDirectory, "RuneshapePriceChecker.exe");
    try { File.Delete(exePath); } catch { }
    try { File.Move(exeNewPath, exePath); } catch { }
}

var resolvedTesseractDataPath = TesseractBootstrapper.ResolveTessDataPath();
AppSettingsBootstrapper.EnsureExists();

// If the app crashed or was closed during a bug report, restore the pre-bug-report
// settings so the app doesn't start with diagnostic mode (Trace logging, etc.).
AppSettingsBootstrapper.TryRecoverBugReportSnapshot();

var host = Host.CreateDefaultBuilder(args)
    .ConfigureHostOptions(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(1);
    })
    .ConfigureAppConfiguration(config =>
    {
        _ = config.SetBasePath(AppContext.BaseDirectory);
        _ = config.AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: true);
        _ = config.AddCommandLine(args);
    })
    .ConfigureServices((context, services) =>
    {
        _ = services.Configure<AppOptions>(context.Configuration.GetSection("App"));
        _ = services.Configure<UpdateOptions>(context.Configuration.GetSection("Update"));
        _ = services.Configure<WindowOptions>(context.Configuration.GetSection("Window"));

        _ = services.AddHostedService<UpdateChecker>();

        _ = services.AddSingleton(dashboardSink);
        _ = services.AddSingleton(dashboardService);
        _ = services.AddSingleton(metricsCollector);

        _ = services.AddOptions<PricingCacheOptions>()
            .Bind(context.Configuration.GetSection("Pricing"))
            .Validate(options =>
                !string.IsNullOrWhiteSpace(options.PricingSource) &&
                !string.IsNullOrWhiteSpace(options.League) &&
                options.IncludedTypes is { Length: > 0 } &&
                options.RefreshInterval > TimeSpan.Zero &&
                options.RedThreshold >= 0m &&
                options.OrangeThreshold > options.RedThreshold &&
                options.GreenThreshold > options.OrangeThreshold &&
                (string.Equals(options.DisplayCurrency, "chaos", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(options.DisplayCurrency, "exalt", StringComparison.OrdinalIgnoreCase)),
                "Pricing configuration is invalid. Check appsettings.json:Pricing values.")
            .ValidateOnStart();
        _ = services.AddOptions<OcrOptions>()
            .Bind(context.Configuration.GetSection("OCR"));
        _ = services.PostConfigure<OcrOptions>(options =>
        {
            options.TesseractDataPath = resolvedTesseractDataPath;
        });

        _ = services.AddHttpClient<PoeNinjaClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        _ = services.AddHttpClient<Poe2ScoutClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        _ = services.AddHttpClient("GitHub", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("RuneshapePriceChecker", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var token = context.Configuration["Update:GitHubToken"];
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        });

        _ = services.AddSingleton<IPricingSource, PricingSourceRouter>();

        _ = services.AddSingleton<Poe2WindowResolutionService>();
        _ = services.AddSingleton<IPoe2WindowResolutionProvider>(sp => sp.GetRequiredService<Poe2WindowResolutionService>());

        _ = services.AddSingleton<OcrLeagueWindowReader>();
        _ = services.AddSingleton<PricingOverlayRenderer>();
        _ = services.AddSingleton(sp =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RuneshapePriceChecker/1.0");
            var logger = sp.GetRequiredService<ILogger<TranslationCache>>();
            return new TranslationCache(client, logger);
        });
        _ = services.AddSingleton<ItemNameTranslator>();

        _ = services.AddSingleton<InMemoryPricingCache>();

        _ = services.AddHostedService(sp => sp.GetRequiredService<Poe2WindowResolutionService>());
        _ = services.AddHostedService<SettingsController>();
        _ = services.AddSingleton<DebugOverlayService>();
        _ = services.AddHostedService(sp => sp.GetRequiredService<DebugOverlayService>());
        _ = services.AddHostedService<PricingCacheRefreshWorker>();
        _ = services.AddHostedService<LeaguePricingWorker>();
    })
    .ConfigureLogging((context, logging) =>
    {
        var logLevelStr = context.Configuration["App:LogLevel"] ?? "Information";
        var minLevel = Enum.TryParse<LogLevel>(logLevelStr, ignoreCase: true, out var parsed)
            ? parsed : LogLevel.Information;

        _ = logging.ClearProviders();
        _ = logging.AddProvider(dashboardLoggerProvider);
        _ = logging.AddProvider(new FileLogProvider());
        _ = logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "HH:mm:ss.fff ";
            options.SingleLine = true;
        });
        CrashLogger.MinimumLogLevel = minLevel;
        _ = logging.AddFilter((category, level) => level >= CrashLogger.MinimumLogLevel);
        _ = logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        _ = logging.AddFilter("Microsoft.Extensions.Http.DefaultHttpClientFactory", LogLevel.Warning);
        _ = logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Error);
    })
    .Build();

// Inject logger into static Poe2ConfigFile so config-file reads are visible in logs
Poe2ConfigFile.SetLogger(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Poe2ConfigFile"));
RpcServiceRunner.SetLogger(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RpcServiceRunner"));
LeagueListService.SetLogger(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LeagueListService"));
LosslessScaling.SetLogger(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LosslessScaling"));
OcrPipeline.SetLogger(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OcrPipeline"));

var debugOverlay = host.Services.GetRequiredService<DebugOverlayService>();
dashboardService.SetReRunSetupTrigger(debugOverlay.RunInitialSetup);

var bugReportLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<BugReportService>();
var ocrOptions = host.Services.GetRequiredService<IOptionsMonitor<OcrOptions>>();
var bugReportService = new BugReportService(dashboardService, bugReportLogger, ocrOptions);
dashboardService.SetBugReportTrigger(bugReportService.StartBugReportFlow);

_ = host.Services.GetRequiredService<IOptionsMonitor<AppOptions>>()
    .OnChange(opts => CrashLogger.MinimumLogLevel = opts.LogLevel);

// Watch for translations.json changes so user edits take effect immediately
var translator = host.Services.GetRequiredService<ItemNameTranslator>();
translator.WatchForChanges();

// Seed metrics collector with config values
try
{
    var metricsCfgPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
    if (File.Exists(metricsCfgPath))
    {
        using var cfgDoc = JsonDocument.Parse(File.ReadAllText(metricsCfgPath));
        var root = cfgDoc.RootElement;
        if (root.TryGetProperty("Pricing", out var pricing))
        {
            if (pricing.TryGetProperty("PricingSource", out var ps))
                metricsCollector.PricingSource = ps.GetString() ?? "poe2scout";
            if (pricing.TryGetProperty("League", out var lg))
                metricsCollector.CurrentLeague = lg.GetString() ?? "";
        }
    }
}
catch { }

// Clean up old directory layouts from before v1.0.2
foreach (var staleDir in new[] { "tesseract", "ocr-debug" })
{
    var path = Path.Combine(AppContext.BaseDirectory, staleDir);
    try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
}

var ocrReader = host.Services.GetRequiredService<OcrLeagueWindowReader>();
_ = Task.Run(() =>
{
    try { ocrReader.Warmup(); }
    catch (Exception ex)
    {
        dashboardSink.Emit($"Tesseract warmup failed: {ex.Message}", "amber");
        dashboardService.SetStatus($"Tesseract warmup failed: {ex.Message}", "amber");
    }
});

dashboardService.SetOnWindowLoaded(() =>
{
    if (debugOverlay.NeedsInitialSetup())
        debugOverlay.RunInitialSetup();
});

try
{
    await host.RunAsync(hostCts.Token).ConfigureAwait(false);
}
catch (Exception ex) when (!hostCts.Token.IsCancellationRequested)
{
    CrashLogger.WriteCrash("Host.RunAsync threw an unhandled exception", ex);
    TrySaveCrashLog();
    throw; // Still let the process terminate — this is a crash
}

dashboardService.Stop();
dashboardService.Dispose();
dashboardLoggerProvider.Dispose();
dashboardSink.Dispose();
hostCts.Dispose();
mutex?.Dispose();

static void TryDeleteFile(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch { }
}

static void TrySaveCrashLog()
{
    try
    {
        // When FileLogProvider is active, flush pending entries to disk so the crash log has full context.
        // Force a GC and wait for pending finalizers so buffered ILogger writes drain.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Thread.Sleep(500);
    }
    catch
    {
        // Best effort
    }
}
