using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RuneshapePriceChecker.App;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.OCR;
using RuneshapePriceChecker.Pricing;
using RuneshapePriceChecker.Startup;
using RuneshapePriceChecker.Tests.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests;

public sealed class ErrorHandlingTests
{
    [Fact]
    public void CrashLogger_WritesCrashLog()
    {
        WithCleanLogDir(dir =>
        {
            ResetCrashLogger();
            var ex = new AccessViolationException("Simulated fatal error");
            CrashLogger.WriteCrash("Test: fatal crash", ex);

            var files = Directory.GetFiles(dir, "*-crash.txt");
            var content = File.ReadAllText(files[0]);
            Assert.Contains("AccessViolationException", content);
            Assert.Contains("--- STACK TRACE ---", content);
        });
    }

    [Fact]
    public void CrashLog_ContainsAllRequiredSections()
    {
        WithCleanLogDir(dir =>
        {
            ResetCrashLogger();
            var ex = new InvalidOperationException("test crash", new ArgumentException("inner"));
            CrashLogger.WriteCrash("Full format test", ex);

            var content = File.ReadAllText(Directory.GetFiles(dir, "*-crash.txt")[0]);
            Assert.Contains("RuneshapePriceChecker Crash Report", content);
            Assert.Contains("--- EXCEPTION ---", content);
            Assert.Contains("--- STACK TRACE ---", content);
            Assert.Contains("--- INNER EXCEPTION ---", content);
            Assert.Contains("--- SYSTEM INFO ---", content);
            Assert.Contains("PID:", content);
            Assert.Contains("CLR:", content);
            Assert.Matches(@"\d{8}-\d{6}\.\d{3}-crash\.txt", Path.GetFileName(Directory.GetFiles(dir, "*-crash.txt")[0]));
        });
    }

    [Fact]
    public void CrashLogger_OnlyOneCrashFilePerSession()
    {
        WithCleanLogDir(dir =>
        {
            ResetCrashLogger();
            CrashLogger.WriteCrash("First", new Exception("a"));
            CrashLogger.WriteCrash("Second", new Exception("b"));
            CrashLogger.WriteCrash("Third", new Exception("c"));
            Assert.Single(Directory.GetFiles(dir, "*-crash.txt"));
        });
    }

    [Fact]
    public void CrashLogger_WritesNullException()
    {
        WithCleanLogDir(dir =>
        {
            ResetCrashLogger();
            CrashLogger.WriteCrash("Null test", null);
            Assert.Contains("Null test", File.ReadAllText(Directory.GetFiles(dir, "*-crash.txt")[0]));
        });
    }

    [Fact]
    public void CrashLogger_WritesAggregateException()
    {
        WithCleanLogDir(dir =>
        {
            ResetCrashLogger();
            CrashLogger.WriteCrash("Aggregate test",
                new AggregateException(new InvalidOperationException("a"), new ArgumentException("b")));

            var content = File.ReadAllText(Directory.GetFiles(dir, "*-crash.txt")[0]);
            Assert.Contains("Inner Exception [0]", content);
            Assert.Contains("Inner Exception [1]", content);
        });
    }
    [Fact]
    public void ErrorContext_ExtractsFileAndLine()
    {
        try { throw new InvalidOperationException("test"); }
        catch (Exception ex)
        {
            var ctx = ErrorContext.FromException(ex);
            Assert.Contains("ErrorHandlingTests.cs", ctx);
            Assert.Contains("InvalidOperationException", ctx);
        }
    }

    [Fact]
    public void ErrorContext_HandlesNullStackTrace()
    {
        var ex = new InvalidOperationException("no stack");
        typeof(Exception).GetField("_stackTrace",
            BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(ex, null);
        Assert.Equal("InvalidOperationException", ErrorContext.FromException(ex));
    }
    [Fact]
    public void Poe2ConfigFile_AccessProperties_DoesNotThrow()
    {
        // Config file may or may not exist in the test environment.
        // All property accesses must return without throwing.
        var ex = Record.Exception(() =>
        {
            _ = Poe2ConfigFile.Language;
            _ = Poe2ConfigFile.IsFullscreen;
            _ = Poe2ConfigFile.UiBrightness;
            _ = Poe2ConfigFile.MouseCursorSize;
        });
        Assert.Null(ex);
    }
    [Fact]
    public void PricingCache_HandlesEmptyDataGracefully()
    {
        var cache = new InMemoryPricingCache(
            new NullPricingSource(),
            new NullOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions
            {
                PricingSource = "poe.ninja",
                League = "Settlers",
                DisplayCurrency = "chaos",
                RedThreshold = 0.5m,
                OrangeThreshold = 1m,
                GreenThreshold = 5m
            }),
            NullLogger<InMemoryPricingCache>.Instance);

        Assert.False(cache.IsReady);
        Assert.Null(cache.TryGetPriceQuote("Chaos Orb", 1));
    }

    [Fact]
    public void PricingCache_HandlesCorruptPriceData_WithoutCrashing()
    {
        var cache = new InMemoryPricingCache(
            new FaultyPricingSource(),
            new NullOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions
            {
                PricingSource = "poe.ninja",
                League = "Settlers",
                DisplayCurrency = "chaos",
                RedThreshold = 0.5m,
                OrangeThreshold = 1m,
                GreenThreshold = 5m
            }),
            NullLogger<InMemoryPricingCache>.Instance);

        // RefreshAsync propagates the error from the pricing source;
        // the worker catches it (tested in PricingCacheRefreshWorker_HandlesRefreshFailure)
        Assert.Throws<HttpRequestException>(() =>
        {
            cache.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        });
        Assert.False(cache.IsReady);
    }
    [Fact]
    public void Poe2ScoutClient_HandlesHttpErrors_WithoutCrashing()
    {
        using var handler = new MockHttpHandler();
        handler.AddErrorResponse("/", HttpStatusCode.InternalServerError);

        var client = new Poe2ScoutClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new NullOptionsMonitor<AppOptions>(new AppOptions()),
            NullLogger<Poe2ScoutClient>.Instance);

        // Must not crash — error is caught internally or propagates as controlled exception
        var ex = Record.Exception(() =>
        {
            client.FetchPricesAsync("Settlers", CancellationToken.None).GetAwaiter().GetResult();
        });
        // Either way, no crash — just verify it completes without throwing something fatal
        Assert.False(ex is AccessViolationException, "Must not crash with AccessViolationException");
        Assert.False(ex is NullReferenceException, "Must not throw NullReferenceException");
    }

    [Fact]
    public void Poe2ScoutClient_HandlesCorruptJson_WithoutCrashing()
    {
        using var handler = new MockHttpHandler();
        handler.AddRawResponse("/", "{{{corrupt json!!!", HttpStatusCode.OK);

        var client = new Poe2ScoutClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new NullOptionsMonitor<AppOptions>(new AppOptions()),
            NullLogger<Poe2ScoutClient>.Instance);

        var ex = Record.Exception(() =>
        {
            client.FetchPricesAsync("Settlers", CancellationToken.None).GetAwaiter().GetResult();
        });
        Assert.False(ex is AccessViolationException, "Must not crash with AccessViolationException");
        Assert.False(ex is NullReferenceException, "Must not throw NullReferenceException");
    }

    [Fact]
    public void Poe2ScoutClient_HandlesTimeout_WithoutCrashing()
    {
        using var handler = new MockHttpHandler();
        handler.AddSlowResponse("/", TimeSpan.FromSeconds(30));

        var client = new Poe2ScoutClient(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/"),
                Timeout = TimeSpan.FromMilliseconds(10)
            },
            new NullOptionsMonitor<AppOptions>(new AppOptions()),
            NullLogger<Poe2ScoutClient>.Instance);

        var ex = Record.Exception(() =>
        {
            client.FetchPricesAsync("Settlers", CancellationToken.None).GetAwaiter().GetResult();
        });
        Assert.False(ex is AccessViolationException, "Must not crash with AccessViolationException");
        Assert.False(ex is NullReferenceException, "Must not throw NullReferenceException");
    }
    [Fact]
    public void PoeNinjaClient_HandlesHttpErrors_WithoutCrashing()
    {
        using var handler = new MockHttpHandler();
        handler.AddErrorResponse("/", HttpStatusCode.InternalServerError);

        var client = new PoeNinjaClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new NullOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions
            {
                PricingSource = "poe.ninja",
                League = "Settlers",
                DisplayCurrency = "chaos",
                RedThreshold = 0.5m,
                OrangeThreshold = 1m,
                GreenThreshold = 5m
            }),
            NullLogger<PoeNinjaClient>.Instance);

        var ex = Record.Exception(() =>
        {
            client.FetchPricesAsync("Settlers", CancellationToken.None).GetAwaiter().GetResult();
        });
        Assert.False(ex is AccessViolationException, "Must not crash with AccessViolationException");
        Assert.False(ex is NullReferenceException, "Must not throw NullReferenceException");
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseItemAndQuantity_HandlesInvalidInput(string? input)
    {
        var method = typeof(LeaguePricingWorker).GetMethod("ParseItemAndQuantity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [input ?? string.Empty]);
        var tuple = (ITuple)result!;
        Assert.NotNull(tuple[0]);
    }
    [Fact]
    public void ItemNameTranslator_HandlesNullName()
    {
        using var translator = new ItemNameTranslator(NullLogger<ItemNameTranslator>.Instance);
        Assert.Null(translator.ToEnglish(null!));
    }

    [Fact]
    public void ItemNameTranslator_HandlesEmptyName()
    {
        using var translator = new ItemNameTranslator(NullLogger<ItemNameTranslator>.Instance);
        Assert.Equal("", translator.ToEnglish(""));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void InMemoryPricingCache_Normalize_HandlesInvalid(string? input)
    {
        Assert.NotNull(InMemoryPricingCache.Normalize(input ?? string.Empty));
    }
    [Fact]
    public void PricingCacheRefreshWorker_HandlesRefreshFailure_WithoutCrashing()
    {
        var opts = new NullOptionsMonitor<PricingCacheOptions>(new PricingCacheOptions
        {
            PricingSource = "poe.ninja",
            League = "Settlers",
            DisplayCurrency = "chaos",
            RedThreshold = 0.5m,
            OrangeThreshold = 1m,
            GreenThreshold = 5m
        });
        var ocrOpts = new NullOptionsMonitor<OcrOptions>(new OcrOptions { Language = "eng" });

        var cache = new InMemoryPricingCache(
            new FaultyPricingSource(), opts,
            NullLogger<InMemoryPricingCache>.Instance);

        using var dashSink = new global::RuneshapePriceChecker.App.Dashboard.DashboardLogSink();
        using var dashService = new global::RuneshapePriceChecker.App.Dashboard.DashboardService(dashSink, null, null);
        using var worker = new PricingCacheRefreshWorker(
            cache, opts, ocrOpts,
            NullLogger<PricingCacheRefreshWorker>.Instance,
            dashService);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(500);

        var ex = Record.Exception(() =>
        {
            worker.StartAsync(cts.Token).GetAwaiter().GetResult();
        });

        // Worker must not crash; only OperationCanceledException on timeout is acceptable
        if (ex is not null)
            Assert.True(ex is OperationCanceledException,
                $"Got {ex.GetType().Name}: {ex.Message}");
    }
    private static void WithCleanLogDir(Action<string> action)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        try { action(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static void ResetCrashLogger()
    {
        typeof(CrashLogger).GetField("_hasCrashed",
            BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, false);
    }
}
file sealed class NullPricingSource : IPricingSource
{
    private static readonly PricingSnapshot EmptySnapshot = new(
        new Dictionary<string, decimal>(),
        new Dictionary<string, (decimal MinChaos, decimal MaxChaos)>(),
        0m, 0m, 0m, 0m, null);

    public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken ct) =>
        Task.FromResult(EmptySnapshot);
}

file sealed class FaultyPricingSource : IPricingSource
{
    public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct) =>
        throw new HttpRequestException("Simulated API failure");

    public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken ct) =>
        throw new HttpRequestException("Simulated API failure");
}

file sealed class NullOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly T _value;
    public NullOptionsMonitor(T value) => _value = value;
    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}


