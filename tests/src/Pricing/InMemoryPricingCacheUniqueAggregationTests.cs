using Microsoft.Extensions.Logging;
using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class InMemoryPricingCacheUniqueAggregationTests
{
    private readonly ILogger<InMemoryPricingCache> _logger = new LoggerFactory().CreateLogger<InMemoryPricingCache>();

    [Fact]
    public async Task Refresh_WithDiverseUniqueItems_BuildsCorrectAggregates()
    {
        var snapshot = CreateDiverseSnapshot();
        var cache = CreateCache(snapshot);

        await cache.RefreshAsync(CancellationToken.None);
        Assert.True(cache.IsReady);

        // Direct lookup of category aggregates
        AssertPriceExists(cache, "Unique Belt", isRange: true);
        AssertPriceExists(cache, "Unique Ring", isRange: true);
        AssertPriceExists(cache, "Unique Amulet", isRange: true);
        AssertPriceExists(cache, "Unique Body Armour", isRange: true);
        AssertPriceExists(cache, "Unique Helmet", isRange: true);
        AssertPriceExists(cache, "Unique Gloves", isRange: true);
        AssertPriceExists(cache, "Unique Boots", isRange: true);
        AssertPriceExists(cache, "Unique Shield", isRange: true);
        AssertPriceExists(cache, "Unique Quiver", isRange: true);
        AssertPriceExists(cache, "Unique Talisman", isRange: true);
        AssertPriceExists(cache, "Unique Weapon", isRange: true);
        AssertPriceExists(cache, "Unique Focus", isRange: true);

        // Global UNIQUE range should exist
        AssertPriceExists(cache, "Unique", isRange: true);

        // Jewellery combined range
        AssertPriceExists(cache, "Unique Jewellery", isRange: true);
    }

    [Fact]
    public async Task Refresh_SpellingVariants_MatchCorrectly()
    {
        var snapshot = CreateDiverseSnapshot();
        var cache = CreateCache(snapshot);
        await cache.RefreshAsync(CancellationToken.None);

        // ARMOUR/ARMOR variants should both work
        AssertPriceExists(cache, "Unique Body Armour", isRange: true);
        AssertPriceExists(cache, "Unique Body Armor", isRange: true);

        // JEWELLERY/JEWELRY variants
        AssertPriceExists(cache, "Unique Jewellery", isRange: true);
        AssertPriceExists(cache, "Unique Jewelry", isRange: true);
    }

    [Fact]
    public async Task Refresh_ItemNotInExplicitLookup_FallsBackToBaseType()
    {
        // Simulate a new unique ring not in the explicit lookup
        var snapshot = new PricingSnapshot(
            ExactPrices: new Dictionary<string, decimal>(),
            UniqueCategoryRanges: new Dictionary<string, (decimal, decimal)>
            {
                ["NEW UNRING"] = (50m, 50m)
            },
            DivineOrbChaosValue: 150m,
            ExaltedOrbChaosValue: 0m,
            CurrencyMinChaos: 0m,
            CurrencyMaxChaos: 0m,
            UniqueItemBaseTypes: new Dictionary<string, string>
            {
                ["NEW UNRING"] = "Topaz Ring"
            }
        );

        var cache = CreateCache(snapshot);
        await cache.RefreshAsync(CancellationToken.None);

        // Should fall back to GetSlotFromBaseType which now handles RING
        AssertPriceExists(cache, "Unique Ring", isRange: true);
    }

    [Fact]
    public async Task Refresh_ItemWithNoBaseType_SkippedFromCategoryButInGlobal()
    {
        var snapshot = new PricingSnapshot(
            ExactPrices: new Dictionary<string, decimal>(),
            UniqueCategoryRanges: new Dictionary<string, (decimal, decimal)>
            {
                ["MYSTERY ITEM"] = (100m, 100m)
            },
            DivineOrbChaosValue: 150m,
            ExaltedOrbChaosValue: 0m,
            CurrencyMinChaos: 0m,
            CurrencyMaxChaos: 0m,
            UniqueItemBaseTypes: new Dictionary<string, string>()
        // No base type for this item
        );

        var cache = CreateCache(snapshot);
        await cache.RefreshAsync(CancellationToken.None);

        // Should still be in global UNIQUE range
        AssertPriceExists(cache, "Unique", isRange: true);

        // But no category-specific range
        var quote = cache.TryGetPriceQuote("Unique Ring");
        Assert.Null(quote); // No ring items had base types
    }

    [Fact]
    public async Task Refresh_MultipleCategories_HaveCorrectRanges()
    {
        var snapshot = CreateDiverseSnapshot();
        var cache = CreateCache(snapshot);
        await cache.RefreshAsync(CancellationToken.None);

        // Direct diagnostic: is "UNIQUE RING" in the cache?
        var ringQuote = cache.TryGetPriceQuote("Unique Ring");
        Assert.NotNull(ringQuote);
        Assert.True(ringQuote!.IsRange);
        Assert.True(ringQuote.RepresentativeChaosValue > 0m,
            $"Unique Ring had value {ringQuote.RepresentativeChaosValue}, label: {ringQuote.Label}");

        // Each category should have a valid range
        var categories = new[] { "Unique Belt", "Unique Amulet", "Unique Helmet", "Unique Gloves",
            "Unique Boots", "Unique Body Armour", "Unique Weapon", "Unique Shield",
            "Unique Quiver", "Unique Focus", "Unique Talisman" };
        foreach (var cat in categories)
        {
            var q = cache.TryGetPriceQuote(cat);
            Assert.NotNull(q);
            Assert.True(q!.IsRange);
            Assert.True(q.RepresentativeChaosValue > 0m, $"{cat} had value {q.RepresentativeChaosValue}");
        }
    }

    private static void AssertPriceExists(InMemoryPricingCache cache, string itemName, bool isRange)
    {
        var quote = cache.TryGetPriceQuote(itemName);
        Assert.NotNull(quote);
        Assert.Equal(isRange, quote!.IsRange);
        Assert.NotEqual("N/A", quote.Label);
        Assert.NotEqual("...", quote.Label);
    }

    private static void AssertRangeIsValid(InMemoryPricingCache cache, string itemName)
    {
        var quote = cache.TryGetPriceQuote(itemName);
        Assert.NotNull(quote);
        Assert.True(quote!.IsRange);
        Assert.True(quote.RepresentativeChaosValue > 0m, $"{itemName} should have positive price");
        Assert.False(quote.Label.StartsWith("N/A"), $"{itemName} label should not be N/A");
    }

    private InMemoryPricingCache CreateCache(PricingSnapshot snapshot)
    {
        var source = new SnapshotPricingSource(snapshot);
        var options = new PricingCacheOptions
        {
            League = "Test",
            DisplayCurrency = "chaos",
            RedThreshold = 0.5m,
            OrangeThreshold = 1.0m,
            GreenThreshold = 5.0m
        };
        var monitor = new StaticOptionsMonitor<PricingCacheOptions>(options);
        return new InMemoryPricingCache(source, monitor, _logger);
    }

    private static PricingSnapshot CreateDiverseSnapshot()
    {
        var uniqueRanges = new Dictionary<string, (decimal, decimal)>();
        var baseTypes = new Dictionary<string, string>();

        // Base types from Poe2Scout Type field — these are what the API actually returns.
        // The key is the normalized unique item name (from Text field), but what matters
        // for GetSlotFromBaseType is the base type string.
        var items = new (string Key, string BaseType, decimal Price)[]
        {
            // Accessories — RING
            ("Iron Ring", "Iron Ring", 10m),
            ("Gold Ring", "Gold Ring", 100m),
            ("Topaz Ring", "Topaz Ring", 12000m),
            ("Sapphire Ring", "Sapphire Ring", 5m),
            // Accessories — AMULET
            ("Amber Amulet", "Amber Amulet", 12m),
            ("Coral Amulet", "Coral Amulet", 15m),
            ("Jade Amulet", "Jade Amulet", 50m),
            // Accessories — BELT
            ("Leather Belt", "Leather Belt", 18000m),
            ("Heavy Belt", "Heavy Belt", 40000m),
            ("Rustic Sash", "Rustic Sash", 5m),
            // Accessories — SHIELD
            ("Tower Shield", "Tower Shield", 80m),
            ("Spiked Shield", "Spiked Shield", 8m),
            // Accessories — QUIVER
            ("Broadhead Quiver", "Broadhead Quiver", 5m),
            // Accessories — FOCUS
            ("Bone Focus", "Bone Focus", 15m),
            // Accessories — TALISMAN
            ("Wereclaw Talisman", "Wereclaw Talisman", 10m),
            // Armour — BODY ARMOUR
            ("Simple Robe", "Simple Robe", 3m),
            ("Glorious Plate", "Glorious Plate", 100m),
            ("Silk Robe", "Silk Robe", 25000m),
            // Armour — HELMET
            ("Leather Hood", "Leather Hood", 2m),
            ("Nightmare Bascinet", "Nightmare Bascinet", 10m),
            // Armour — GLOVES
            ("Iron Gauntlets", "Iron Gauntlets", 5m),
            ("Slink Gloves", "Slink Gloves", 500m),
            // Armour — BOOTS
            ("Wool Shoes", "Wool Shoes", 1m),
            ("Sharkskin Boots", "Sharkskin Boots", 8m),
            // Weapon — ONE HAND MACE
            ("Driftwood Club", "Driftwood Club", 2m),
            ("Royal Sceptre", "Royal Sceptre", 100m),
            // Weapon — TWO HAND MACE
            ("Imperial Maul", "Imperial Maul", 20m),
            // Weapon — BOW
            ("Crude Bow", "Crude Bow", 3m),
            ("Short Bow", "Short Bow", 5m),
            // Weapon — CROSSBOW
            ("Makeshift Crossbow", "Makeshift Crossbow", 3m),
            // Weapon — QUARTERSTAFF
            ("Iron Staff", "Iron Staff", 10m),
            // Weapon — SPEAR
            ("Wooden Spear", "Wooden Spear", 5m),
            // Weapon — STAFF
            ("Long Staff", "Long Staff", 8m),
            // Weapon — WAND
            ("Driftwood Wand", "Driftwood Wand", 2m),
            // Weapon — SCEPTRE
            ("Bone Sceptre", "Bone Sceptre", 8m),
        };

        foreach (var (key, baseType, price) in items)
        {
            var normalized = InMemoryPricingCache.Normalize(key);
            uniqueRanges[normalized] = (price, price);
            baseTypes[normalized] = baseType;
        }

        return new PricingSnapshot(
            new Dictionary<string, decimal> { ["Divine Orb"] = 150m, ["Exalted Orb"] = 0.05m },
            uniqueRanges,
            DivineOrbChaosValue: 150m,
            ExaltedOrbChaosValue: 0.05m,
            CurrencyMinChaos: 0.9m,
            CurrencyMaxChaos: 11430m,
            UniqueItemBaseTypes: baseTypes
        );
    }

    private static void AddUnique(
        Dictionary<string, (decimal, decimal)> ranges,
        Dictionary<string, string> baseTypes,
        string name, string baseType, decimal price)
    {
        var normalized = InMemoryPricingCache.Normalize(name);
        ranges[normalized] = (price, price);
        if (!string.IsNullOrWhiteSpace(baseType))
            baseTypes[normalized] = baseType;
    }

    private sealed class SnapshotPricingSource(PricingSnapshot snapshot) : IPricingSource
    {
        public Task<IReadOnlyList<string>> FetchLeaguesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<PricingSnapshot> FetchPricesAsync(string league, CancellationToken ct) =>
            Task.FromResult(snapshot);
    }
}
