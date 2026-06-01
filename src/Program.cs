using RuneshapePriceChecker.App;
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

DebugConsoleWindow.TryOpen();

var resolvedTesseractPath = TesseractBootstrapper.EnsureInstalled("tesseract");

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration(config =>
    {
        config.AddJsonFile("src/appsettings.json", optional: false, reloadOnChange: false);
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<AppOptions>(context.Configuration.GetSection("App"));

        services.AddOptions<PricingCacheOptions>()
            .Bind(context.Configuration.GetSection("Pricing"))
            .Validate(options =>
                !string.IsNullOrWhiteSpace(options.PoeNinjaBaseUrl) &&
                !string.IsNullOrWhiteSpace(options.ExchangeOverviewPath) &&
                !string.IsNullOrWhiteSpace(options.StashItemOverviewPath) &&
                !string.IsNullOrWhiteSpace(options.League) &&
                options.IncludedTypes is { Length: > 0 } &&
                options.RefreshInterval > TimeSpan.Zero &&
                options.RedThresholdChaos >= 0m &&
                options.OrangeThresholdChaos > options.RedThresholdChaos &&
                options.GreenThresholdChaos > options.OrangeThresholdChaos,
                "Pricing configuration is invalid. Check src/appsettings.json:Pricing values.")
            .ValidateOnStart();
        services.AddOptions<OcrOptions>()
            .Bind(context.Configuration.GetSection("OCR"));
        services.PostConfigure<OcrOptions>(options =>
        {
            options.TesseractExePath = resolvedTesseractPath;
        });

        services.AddHttpClient<IPoeNinjaClient, PoeNinjaClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddSingleton<Poe2WindowResolutionService>();
        services.AddSingleton<IPoe2WindowResolutionProvider>(sp => sp.GetRequiredService<Poe2WindowResolutionService>());

        services.AddSingleton<ILeagueWindowReader, OcrLeagueWindowReader>();
        services.AddSingleton<IOverlayRenderer, ConsoleOverlayRenderer>();
        services.AddSingleton<IPricingCache, InMemoryPricingCache>();

        services.AddHostedService(sp => sp.GetRequiredService<Poe2WindowResolutionService>());
        services.AddHostedService<SettingsController>();
        services.AddHostedService<OcrCaptureBoundsOverlayService>();
        services.AddHostedService<PricingCacheRefreshWorker>();
        services.AddHostedService<LeaguePricingWorker>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
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

await host.RunAsync().ConfigureAwait(false);
