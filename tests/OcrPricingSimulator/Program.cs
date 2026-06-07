using System.Globalization;
using System.Text.Json;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var options = ParseArgs(args);
var inputItems = LoadInputItems(options.InputFile, options.Items);
if (inputItems.Count == 0)
{
    Console.Error.WriteLine("No items provided. Use --item and/or --input-file.");
    return 1;
}

var poeClient = await CreateClientAsync(options).ConfigureAwait(false);
var simulatorPricingOptions = new PricingCacheOptions
{
    PoeNinjaBaseUrl = options.BaseUrl,
    League = options.League,
    IncludedTypes = options.Types.ToArray(),
    DisplayCurrency = options.DisplayCurrency
};
var cache = new InMemoryPricingCache(poeClient, new StaticOptionsMonitor<PricingCacheOptions>(simulatorPricingOptions));
await cache.RefreshAsync(CancellationToken.None).ConfigureAwait(false);

var rows = new List<ResultRow>(inputItems.Count);
foreach (var rawItem in inputItems)
{
    var parsed = ItemNameParser.ParseDetectedItem(rawItem);
    var normalized = InMemoryPricingCache.Normalize(parsed.Name);
    var quote = cache.TryGetPriceQuote(parsed.Name, parsed.Quantity);

    rows.Add(new ResultRow(
        rawItem,
        parsed.Quantity,
        parsed.Name,
        normalized,
        quote?.Label ?? string.Empty,
        quote is null ? "n/a" : quote.IsRange ? "range" : "exact"));
}

PrintRows(rows);
return 0;

static SimOptions ParseArgs(string[] args)
{
    var options = new SimOptions();

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        switch (arg)
        {
            case "--league":
                options.League = ReadValue(args, ref i, arg);
                break;
            case "--base-url":
                options.BaseUrl = ReadValue(args, ref i, arg);
                break;
            case "--types":
                options.Types = ReadValue(args, ref i, arg)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                break;
            case "--input-file":
                options.InputFile = ReadValue(args, ref i, arg);
                break;
            case "--item":
                options.Items.Add(ReadValue(args, ref i, arg));
                break;
            case "--mock-file":
                options.MockFile = ReadValue(args, ref i, arg);
                break;
            case "--display-currency":
                options.DisplayCurrency = ReadValue(args, ref i, arg);
                break;
            default:
                throw new InvalidOperationException($"Unknown argument: {arg}");
        }
    }

    return options;
}

static string ReadValue(string[] args, ref int index, string optionName)
{
    if (index + 1 >= args.Length)
    {
        throw new InvalidOperationException($"Missing value for {optionName}.");
    }

    index++;
    return args[index];
}

static List<string> LoadInputItems(string? inputFile, List<string> inlineItems)
{
    var items = new List<string>();

    foreach (var item in inlineItems)
    {
        if (!string.IsNullOrWhiteSpace(item))
        {
            items.Add(item.Trim());
        }
    }

    if (!string.IsNullOrWhiteSpace(inputFile))
    {
        if (!File.Exists(inputFile))
        {
            throw new FileNotFoundException($"Input file not found: {inputFile}");
        }

        foreach (var line in File.ReadLines(inputFile))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            items.Add(trimmed);
        }
    }

    return items;
}

static async Task<IPoeNinjaClient> CreateClientAsync(SimOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.MockFile))
    {
        return new MockPoeNinjaClient(await LoadMockSnapshotAsync(options.MockFile).ConfigureAwait(false));
    }

    var pricingOptions = new PricingCacheOptions
    {
        PoeNinjaBaseUrl = options.BaseUrl,
        League = options.League,
        IncludedTypes = options.Types.ToArray(),
        DisplayCurrency = options.DisplayCurrency
    };

    var optionsMonitor = new StaticOptionsMonitor<PricingCacheOptions>(pricingOptions);
    var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    return new PoeNinjaClient(httpClient, optionsMonitor, NullLogger<PoeNinjaClient>.Instance);
}

static async Task<PoeNinjaPricingSnapshot> LoadMockSnapshotAsync(string mockFile)
{
    if (!File.Exists(mockFile))
    {
        throw new FileNotFoundException($"Mock file not found: {mockFile}");
    }

    await using var stream = File.OpenRead(mockFile);
    using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
    if (document.RootElement.ValueKind != JsonValueKind.Array)
    {
        throw new InvalidOperationException("Mock file must be a JSON array.");
    }

    var exactPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    var uniqueCategoryRanges = new Dictionary<string, (decimal MinChaos, decimal MaxChaos)>(StringComparer.OrdinalIgnoreCase);
    decimal currencyMinChaos = 0m;
    decimal currencyMaxChaos = 0m;

    foreach (var row in document.RootElement.EnumerateArray())
    {
        if (!row.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
        {
            continue;
        }

        if (!row.TryGetProperty("price", out var priceProp) || !TryReadDecimal(priceProp, out var price) || price <= 0)
        {
            continue;
        }

        var name = nameProp.GetString();
        if (string.IsNullOrWhiteSpace(name))
        {
            continue;
        }

        var normalized = InMemoryPricingCache.Normalize(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            continue;
        }

        exactPrices[normalized] = price;

        if (currencyMinChaos == 0m || price < currencyMinChaos)
            currencyMinChaos = price;
        if (price > currencyMaxChaos)
            currencyMaxChaos = price;

        if (row.TryGetProperty("category", out var categoryProp) && categoryProp.ValueKind == JsonValueKind.String)
        {
            var category = categoryProp.GetString();
            if (!string.IsNullOrWhiteSpace(category))
            {
                var categoryKey = InMemoryPricingCache.Normalize($"Unique {category}");
                AddOrUpdateRange(uniqueCategoryRanges, categoryKey, price);
            }
        }
    }

    var divineOrbChaosValue = exactPrices.TryGetValue("DIVINE ORB", out var divineValue) && divineValue > 0m
        ? divineValue
        : 150m;

    var exaltedOrbChaosValue = exactPrices.TryGetValue("EXALTED ORB", out var exaltedValue) && exaltedValue > 0m
        ? exaltedValue
        : 0m;

    return new PoeNinjaPricingSnapshot(exactPrices, uniqueCategoryRanges, divineOrbChaosValue, exaltedOrbChaosValue, currencyMinChaos, currencyMaxChaos);
}

static bool TryReadDecimal(JsonElement element, out decimal value)
{
    if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
    {
        return true;
    }

    if (element.ValueKind == JsonValueKind.String)
    {
        var text = element.GetString();
        if (!string.IsNullOrWhiteSpace(text) &&
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
    }

    value = 0m;
    return false;
}

static void AddOrUpdateRange(Dictionary<string, (decimal MinChaos, decimal MaxChaos)> table, string key, decimal value)
{
    if (string.IsNullOrWhiteSpace(key))
    {
        return;
    }

    if (table.TryGetValue(key, out var existing))
    {
        table[key] = (Math.Min(existing.MinChaos, value), Math.Max(existing.MaxChaos, value));
    }
    else
    {
        table[key] = (value, value);
    }
}

static void PrintRows(List<ResultRow> rows)
{
    Console.WriteLine("Detected | Qty | Parsed Name | Normalized | Quote | Kind");
    Console.WriteLine("----|-----|-----|-----|-----|-----");

    foreach (var row in rows)
    {
        Console.WriteLine(
            $"{row.Detected} | {row.ParsedQuantity} | {row.ParsedName} | {row.Normalized} | {row.QuoteLabel} | {row.Kind}");
    }
}

file sealed class SimOptions
{
    public string League { get; set; } = "Runes of Aldur";
    public string BaseUrl { get; set; } = "https://poe.ninja";
    public List<string> Types { get; set; } = ["Currency", "Expedition", "UncutGems", "Runes", "Verisium", "UniqueWeapons", "UniqueArmours", "UniqueAccessories"];
    public string? InputFile { get; set; }
    public List<string> Items { get; } = [];
    public string? MockFile { get; set; }
    public string DisplayCurrency { get; set; } = "exalt";
}

file sealed record ResultRow(
    string Detected,
    int ParsedQuantity,
    string ParsedName,
    string Normalized,
    string QuoteLabel,
    string Kind);

file sealed class MockPoeNinjaClient(PoeNinjaPricingSnapshot snapshot) : IPoeNinjaClient
{
    public Task<PoeNinjaPricingSnapshot> FetchCurrentPricesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(snapshot);
    }
}

file sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = currentValue;

    public T Get(string? name)
    {
        return CurrentValue;
    }

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        return null;
    }
}
