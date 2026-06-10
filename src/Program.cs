using System.Diagnostics;
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
using Microsoft.Extensions.Logging.Console;

var mutex = new Mutex(true, @"Global\RuneshapePriceChecker_SingleInstance", out var createdNew);
if (!createdNew)
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
            try { proc.Kill(); proc.WaitForExit(3000); } catch { }
        }

        Thread.Sleep(500);

        mutex.Dispose();
        mutex = new Mutex(true, @"Global\RuneshapePriceChecker_SingleInstance", out createdNew);
    }
    else
    {
        mutex.Dispose();
        return;
    }
}

var dashboardSink = new DashboardLogSink();
var configPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
var dashboardLoggerProvider = new DashboardLoggerProvider(dashboardSink, configPath);
var dashboardService = new DashboardService(dashboardSink);
var hostCts = new CancellationTokenSource();
dashboardService.SetOnWindowClosed(hostCts.Cancel);
dashboardService.Start();

TryDeleteFile(Path.Combine(AppContext.BaseDirectory, "Update.exe.old"));

var updaterNewPath = Path.Combine(AppContext.BaseDirectory, "Update.exe.new");
if (File.Exists(updaterNewPath))
{
    var updaterPath = Path.Combine(AppContext.BaseDirectory, "Update.exe");
    try { File.Delete(updaterPath); } catch { }
    try { File.Move(updaterNewPath, updaterPath); } catch { }
}

var resolvedTesseractDataPath = TesseractBootstrapper.ResolveTessDataPath();
AppSettingsBootstrapper.EnsureExists();

var host = Host.CreateDefaultBuilder(args)
    .ConfigureHostOptions(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(2);
    })
    .ConfigureAppConfiguration(config =>
    {
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: false);
        config.AddCommandLine(args);
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<AppOptions>(context.Configuration.GetSection("App"));
        services.Configure<UpdateOptions>(context.Configuration.GetSection("Update"));
        services.Configure<WindowOptions>(context.Configuration.GetSection("Window"));

        services.AddHostedService<UpdateChecker>();

        services.AddSingleton(dashboardSink);
        services.AddSingleton(dashboardService);

        services.AddOptions<PricingCacheOptions>()
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
        services.AddOptions<OcrOptions>()
            .Bind(context.Configuration.GetSection("OCR"));
        services.PostConfigure<OcrOptions>(options =>
        {
            options.TesseractDataPath = resolvedTesseractDataPath;
        });

        services.AddHttpClient<PoeNinjaClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient<Poe2ScoutClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddSingleton<IPricingSource, PricingSourceRouter>();

        services.AddSingleton<Poe2WindowResolutionService>();
        services.AddSingleton<IPoe2WindowResolutionProvider>(sp => sp.GetRequiredService<Poe2WindowResolutionService>());

        services.AddSingleton<ILeagueWindowReader, OcrLeagueWindowReader>();
        services.AddSingleton<IOverlayRenderer, ConsoleOverlayRenderer>();
        services.AddSingleton<IPricingCache, InMemoryPricingCache>();

        services.AddHostedService(sp => sp.GetRequiredService<Poe2WindowResolutionService>());
        services.AddHostedService<SettingsController>();
        services.AddSingleton<DebugOverlayService>();
        services.AddHostedService(sp => sp.GetRequiredService<DebugOverlayService>());
        services.AddHostedService<PricingCacheRefreshWorker>();
        services.AddHostedService<LeaguePricingWorker>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddProvider(dashboardLoggerProvider);
        logging.AddProvider(new FileLogProvider());
        logging.SetMinimumLevel(LogLevel.Trace);
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        logging.AddFilter("Microsoft.Extensions.Http.DefaultHttpClientFactory", LogLevel.Warning);
        logging.AddConsole(options =>
        {
            options.FormatterName = CompactConsoleFormatter.FormatterName;
        });
        logging.AddConsoleFormatter<CompactConsoleFormatter, SimpleConsoleFormatterOptions>(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    })
    .Build();

var debugOverlay = host.Services.GetRequiredService<DebugOverlayService>();
dashboardService.SetReRunSetupTrigger(debugOverlay.RunInitialSetup);
dashboardService.SetOnWindowLoaded(() =>
{
    if (debugOverlay.NeedsInitialSetup())
        debugOverlay.RunInitialSetup();
});

await host.RunAsync(hostCts.Token).ConfigureAwait(false);

dashboardService.Stop();
dashboardService.Dispose();
mutex.Dispose();

static void TryDeleteFile(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch { }
}
