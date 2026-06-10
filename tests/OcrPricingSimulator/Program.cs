using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;

var options = ParseArgs(args);
var inputItems = LoadInputItems(options.InputFile, options.Items);

if (inputItems.Count == 0)
{
    Console.Error.WriteLine("No items provided. Use --item and/or --input-file.");
    return 1;
}

var pricingSource = await CreatePricingSourceAsync(options);
var cache = new InMemoryPricingCache(
    pricingSource,
    new StaticOptionsMonitor<PricingCacheOptions>(options.ToCacheOptions()),
    NullLogger<InMemoryPricingCache>.Instance);

await cache.RefreshAsync(CancellationToken.None);

Console.WriteLine($"Source: {options.Source}");
Console.WriteLine($"League: {options.League}");
Console.WriteLine($"Items:  {inputItems.Count}");
Console.WriteLine($"Display: {options.DisplayCurrency}");

if (options.Verbose)
{
    DumpCacheSnapshot(cache, inputItems);
}

Console.WriteLine();
RunTests(cache, inputItems);

return 0;

static int RunTests(InMemoryPricingCache cache, List<string> inputItems)
{
    var passed = 0;
    var failed = 0;

    foreach (var rawItem in inputItems)
    {
        var parsed = ItemNameParser.ParseDetectedItem(rawItem);
        var normalized = InMemoryPricingCache.Normalize(parsed.Name);
        var quote = cache.TryGetPriceQuote(parsed.Name, parsed.Quantity);

        var label = quote?.Label ?? "N/A";
        var kind = quote is null ? "n/a" : quote.IsRange ? "range" : "exact";
        var match = quote?.MatchDetail ?? "";

        Console.Write($"{rawItem,-45} -> {label,-18} [{kind,-5}]");
        if (!string.IsNullOrEmpty(match))
            Console.Write($" {match}");
        Console.WriteLine();

        if (label == "N/A" || label == "...")
            failed++;
        else
            passed++;
    }

    Console.WriteLine();
    Console.WriteLine($"Results: {passed} priced, {failed} N/A");
    return failed > 0 ? 1 : 0;
}

static void DumpCacheSnapshot(InMemoryPricingCache cache, List<string> inputItems)
{
    var snapshot = cache.TrySnapshot();
    if (snapshot is null)
    {
        Console.WriteLine("  [Snapshot not available]");
        return;
    }

    Console.WriteLine($"  Exact prices: {snapshot.Value.ExactCount}");
    Console.WriteLine($"  Category ranges: {snapshot.Value.CategoryRangeCount}");
    Console.WriteLine($"  Divine: {snapshot.Value.DivineValue:F2}c  Exalt: {snapshot.Value.ExaltValue:F2}c");
    Console.WriteLine($"  Currency range: {snapshot.Value.CurrencyMin:F2}c - {snapshot.Value.CurrencyMax:F2}c");

    foreach (var rawItem in inputItems)
    {
        var parsed = ItemNameParser.ParseDetectedItem(rawItem);
        var normalized = InMemoryPricingCache.Normalize(parsed.Name);
        var exact = snapshot.Value.ExactPrices.TryGetValue(normalized, out var ep) ? $"{ep:F2}c" : "-";
        Console.WriteLine($"    {normalized,-35} exact={exact}");
    }
}

static async Task<IPricingSource> CreatePricingSourceAsync(SimOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.MockFile))
    {
        var snapshot = await LoadMockSnapshotAsync(options.MockFile);
        return new MockPricingSource(snapshot);
    }

    if (options.Source.Equals("poe2scout", StringComparison.OrdinalIgnoreCase))
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return new Poe2ScoutClient(httpClient, NullLogger<Poe2ScoutClient>.Instance);
    }

    var ninjaHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var ninjaOptions = new StaticOptionsMonitor<PricingCacheOptions>(options.ToCacheOptions());
    return new PoeNinjaClient(ninjaHttpClient, ninjaOptions, NullLogger<PoeNinjaClient>.Instance);
}

static async Task<PricingSnapshot> LoadMockSnapshotAsync(string mockFile)
{
    if (!File.Exists(mockFile))
        throw new FileNotFoundException($"Mock file not found: {mockFile}");

    await using var stream = File.OpenRead(mockFile);
    using var document = await JsonDocument.ParseAsync(stream);

    var root = document.RootElement;

    var exactPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    var uniqueCategoryRanges = new Dictionary<string, (decimal MinChaos, decimal MaxChaos)>(StringComparer.OrdinalIgnoreCase);
    decimal currencyMinChaos = 0m, currencyMaxChaos = 0m;

    decimal divineValue = 0m, exaltValue = 0m;

    foreach (var row in root.EnumerateArray())
    {
        if (!row.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            continue;
        if (!row.TryGetProperty("price", out var priceProp) || !TryReadDecimal(priceProp, out var price) || price <= 0)
            continue;

        var name = nameProp.GetString();
        if (string.IsNullOrWhiteSpace(name)) continue;

        if (string.Equals(name, "Divine Orb", StringComparison.OrdinalIgnoreCase))
            divineValue = price;
        if (string.Equals(name, "Exalted Orb", StringComparison.OrdinalIgnoreCase))
            exaltValue = price;

        var isUnique = row.TryGetProperty("category", out var catProp) &&
                       catProp.ValueKind == JsonValueKind.String &&
                       !string.IsNullOrWhiteSpace(catProp.GetString());

        if (isUnique)
        {
            var category = catProp.GetString()!;
            var normName = InMemoryPricingCache.Normalize(name);
            if (!string.IsNullOrWhiteSpace(normName))
                AddOrUpdateRange(uniqueCategoryRanges, normName, price);
        }
        else
        {
            exactPrices[name] = price;
            if (currencyMinChaos == 0m || price < currencyMinChaos) currencyMinChaos = price;
            if (price > currencyMaxChaos) currencyMaxChaos = price;
        }
    }

    return new PricingSnapshot(exactPrices, uniqueCategoryRanges, divineValue, exaltValue, currencyMinChaos, currencyMaxChaos);
}

static bool TryReadDecimal(JsonElement element, out decimal value)
{
    if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        return true;
    if (element.ValueKind == JsonValueKind.String)
    {
        var text = element.GetString();
        if (!string.IsNullOrWhiteSpace(text) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;
    }
    value = 0m;
    return false;
}

static void AddOrUpdateRange(Dictionary<string, (decimal MinChaos, decimal MaxChaos)> table, string key, decimal value)
{
    if (string.IsNullOrWhiteSpace(key)) return;
    if (table.TryGetValue(key, out var existing))
        table[key] = (Math.Min(existing.MinChaos, value), Math.Max(existing.MaxChaos, value));
    else
        table[key] = (value, value);
}

static SimOptions ParseArgs(string[] args)
{
    var options = new SimOptions();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--league": options.League = ReadValue(args, ref i); break;
            case "--source": options.Source = ReadValue(args, ref i); break;
            case "--types": options.Types = ReadValue(args, ref i).Split(',', StringSplitOptions.TrimEntries).ToList(); break;
            case "--input-file": options.InputFile = ReadValue(args, ref i); break;
            case "--item": options.Items.Add(ReadValue(args, ref i)); break;
            case "--mock-file": options.MockFile = ReadValue(args, ref i); break;
            case "--display-currency": options.DisplayCurrency = ReadValue(args, ref i); break;
            case "--verbose": options.Verbose = true; break;
            default: throw new InvalidOperationException($"Unknown argument: {args[i]}");
        }
    }
    return options;
}

static string ReadValue(string[] args, ref int index)
{
    if (index + 1 >= args.Length) throw new InvalidOperationException("Missing value.");
    return args[++index];
}

static List<string> LoadInputItems(string? inputFile, List<string> inlineItems)
{
    var items = new List<string>();
    foreach (var item in inlineItems)
        if (!string.IsNullOrWhiteSpace(item)) items.Add(item.Trim());
    if (!string.IsNullOrWhiteSpace(inputFile))
    {
        foreach (var line in File.ReadLines(inputFile))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                items.Add(trimmed);
        }
    }
    return items;
}

internal sealed class SimOptions
{
    public string League { get; set; } = "Runes of Aldur";
    public string Source { get; set; } = "poe2scout";
    public List<string> Types { get; set; } = [];
    public string? InputFile { get; set; }
    public List<string> Items { get; } = [];
    public string? MockFile { get; set; }
    public string DisplayCurrency { get; set; } = "exalt";
    public bool Verbose { get; set; }

    public PricingCacheOptions ToCacheOptions() => new()
    {
        League = League,
        DisplayCurrency = DisplayCurrency,
        PricingSource = Source,
        IncludedTypes = Types.Count > 0 ? Types.ToArray() : null
    };
}

internal sealed class MockPricingSource(PricingSnapshot snapshot) : IPricingSource
{
    public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken ct)
        => Task.FromResult(snapshot);
}

internal static class StaticOptionsMonitor
{
    public static IOptionsMonitor<T> Create<T>(T value) where T : class => new StaticOptionsMonitor<T>(value);
}

internal sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T> where T : class
{
    public T CurrentValue => currentValue;
    public T Get(string? name) => currentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
