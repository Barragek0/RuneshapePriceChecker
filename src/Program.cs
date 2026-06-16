using System.Diagnostics;
using System.Text.Json;
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
    // Also check config file — post-update restarts won't have CLI args
    try
    {
        var cfgPath = Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");
        if (File.Exists(cfgPath))
        {
            var cfgJson = JsonDocument.Parse(File.ReadAllText(cfgPath));
            if (cfgJson.RootElement.TryGetProperty("App", out var app) &&
                app.TryGetProperty("SuppressAlreadyRunningWarning", out var sw) &&
                sw.ValueKind == JsonValueKind.True)
                suppressWarning = true;
        }
    }
    catch { }
}

var mutex = new Mutex(true, @"Global\RuneshapePriceChecker_SingleInstance", out var createdNew);
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
TryDeleteFile(Path.Combine(AppContext.BaseDirectory, "RuneshapePriceChecker.exe.old"));

var updaterNewPath = Path.Combine(AppContext.BaseDirectory, "Update.exe.new");
if (File.Exists(updaterNewPath))
{
    var updaterPath = Path.Combine(AppContext.BaseDirectory, "Update.exe");
    try { File.Delete(updaterPath); } catch { }
    try { File.Move(updaterNewPath, updaterPath); } catch { }
}

var exeNewPath = Path.Combine(AppContext.BaseDirectory, "RuneshapePriceChecker.exe.new");
if (File.Exists(exeNewPath))
{
    var exePath = Path.Combine(AppContext.BaseDirectory, "RuneshapePriceChecker.exe");
    try { File.Delete(exePath); } catch { }
    try { File.Move(exeNewPath, exePath); } catch { }
}

var resolvedTesseractDataPath = TesseractBootstrapper.ResolveTessDataPath();
AppSettingsBootstrapper.EnsureExists();

var host = Host.CreateDefaultBuilder(args)
    .ConfigureHostOptions(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(1);
    })
    .ConfigureAppConfiguration(config =>
    {
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: true);
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
        services.AddHttpClient("GitHub", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("RuneshapePriceChecker", "1.0"));
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var token = context.Configuration["Update:GitHubToken"];
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        });

        services.AddSingleton<IPricingSource, PricingSourceRouter>();

        services.AddSingleton<Poe2WindowResolutionService>();
        services.AddSingleton<IPoe2WindowResolutionProvider>(sp => sp.GetRequiredService<Poe2WindowResolutionService>());

        services.AddSingleton<ILeagueWindowReader, OcrLeagueWindowReader>();
        services.AddSingleton<IOverlayRenderer, PricingOverlayRenderer>();
        services.AddHttpClient<ItemNameTranslator>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<ItemNameTranslator>();

        services.AddSingleton<IPricingCache, InMemoryPricingCache>();

        services.AddHostedService(sp => sp.GetRequiredService<Poe2WindowResolutionService>());
        services.AddHostedService<SettingsController>();
        services.AddSingleton<DebugOverlayService>();
        services.AddHostedService(sp => sp.GetRequiredService<DebugOverlayService>());
        services.AddHostedService<PricingCacheRefreshWorker>();
        services.AddHostedService<LeaguePricingWorker>();
    })
    .ConfigureLogging((context, logging) =>
    {
        var logLevelStr = context.Configuration["App:LogLevel"] ?? "Information";
        var minLevel = Enum.TryParse<LogLevel>(logLevelStr, ignoreCase: true, out var parsed)
            ? parsed : LogLevel.Information;

        logging.ClearProviders();
        logging.AddProvider(dashboardLoggerProvider);
        logging.AddProvider(new FileLogProvider());
        logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "HH:mm:ss.fff ";
            options.SingleLine = true;
        });
        logging.SetMinimumLevel(minLevel);
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        logging.AddFilter("Microsoft.Extensions.Http.DefaultHttpClientFactory", LogLevel.Warning);
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Error);
    })
    .Build();

var debugOverlay = host.Services.GetRequiredService<DebugOverlayService>();
dashboardService.SetReRunSetupTrigger(debugOverlay.RunInitialSetup);

var ocrReader = host.Services.GetRequiredService<ILeagueWindowReader>();
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

await host.RunAsync(hostCts.Token).ConfigureAwait(false);

dashboardService.Stop();
dashboardService.Dispose();
mutex.Dispose();

static void TryDeleteFile(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch { }
}
