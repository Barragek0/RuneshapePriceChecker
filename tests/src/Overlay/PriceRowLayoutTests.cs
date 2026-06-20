using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;
using RuneshapePriceChecker.Overlay;
using Xunit;

namespace RuneshapePriceChecker.Tests.Overlay;

public class PriceRowLayoutTests
{
    private static readonly PricingCacheOptions TestOptions = new()
    {
        RedThreshold = 5m,
        OrangeThreshold = 20m,
        GreenThreshold = 50m,
        League = "Standard",
        PricingSource = "test",
        RefreshInterval = TimeSpan.Zero
    };

    private static readonly string[] ChaosOrbItem = ["1x Chaos Orb"];
    private static readonly int[] SingleCount = [100];
    private static readonly string[] UniqueRingItem = ["1x Unique Ring"];
    private static readonly int[] FiftyCount = [50];
    private static readonly string[] UnknownItem = ["1x Unknown Item"];
    private static readonly int[] ZeroCount = [0];

    [Fact]
    public void Build_EmptySnapshot_ReturnsEmpty()
    {
        var snapshot = new LeagueWindowSnapshot(
            [],
            DateTimeOffset.UtcNow,
            [],
            InterfaceDetected: true,
            CaptureMethod: null);

        var rows = new List<Rectangle>();
        var prices = new Dictionary<string, PriceQuote?>();

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        Assert.Empty(result);
    }

    [Fact]
    public void Build_SingleItemWithExactPrice_ReturnsOneEntry()
    {
        var snapshot = new LeagueWindowSnapshot(
            ChaosOrbItem,
            DateTimeOffset.UtcNow,
            SingleCount,
            InterfaceDetected: true,
            CaptureMethod: null);

        var rows = new List<Rectangle> { new(0, 100, 300, 20) };
        var prices = new Dictionary<string, PriceQuote?>
        {
            ["1x Chaos Orb"] = new PriceQuote("1.0", 1.0m, false)
        };

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        _ = Assert.Single(result);
        Assert.Equal(100, result[0].RowY);
        _ = Assert.Single(result[0].Segments);
    }

    [Fact]
    public void Build_ItemWithRangePrice_ReturnsMultipleSegments()
    {
        var snapshot = new LeagueWindowSnapshot(
            UniqueRingItem,
            DateTimeOffset.UtcNow,
            FiftyCount,
            InterfaceDetected: true,
            CaptureMethod: null);

        var rows = new List<Rectangle> { new(0, 50, 300, 20) };
        var prices = new Dictionary<string, PriceQuote?>
        {
            ["1x Unique Ring"] = new PriceQuote("0.5 - 5.0", 5.0m, true)
        };

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        _ = Assert.Single(result);
        Assert.True(result[0].Segments.Count >= 2, "Range price should produce multiple segments");
    }

    [Fact]
    public void Build_UnpricedItem_Skipped()
    {
        var snapshot = new LeagueWindowSnapshot(
            UnknownItem,
            DateTimeOffset.UtcNow,
            ZeroCount,
            InterfaceDetected: true,
            CaptureMethod: null);

        var rows = new List<Rectangle> { new(0, 0, 300, 20) };
        var prices = new Dictionary<string, PriceQuote?>
        {
            ["1x Unknown Item"] = null
        };

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        Assert.Empty(result);
    }

    [Fact]
    public void Build_RangeWithoutSeparator_ReturnsSingleSegment()
    {
        var snapshot = new LeagueWindowSnapshot(
            ChaosOrbItem, DateTimeOffset.UtcNow, SingleCount,
            InterfaceDetected: true, CaptureMethod: null);

        var rows = new List<Rectangle> { new(0, 100, 300, 20) };
        var prices = new Dictionary<string, PriceQuote?>
        {
            ["1x Chaos Orb"] = new PriceQuote("1.0c", 1.0m, true) // IsRange=true but no " -" separator
        };

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        _ = Assert.Single(result);
        _ = Assert.Single(result[0].Segments); // Falls back to single segment
    }

    [Fact]
    public void Build_RangeWithMultipleDashes_HandlesFirstOnly()
    {
        var snapshot = new LeagueWindowSnapshot(
            UniqueRingItem, DateTimeOffset.UtcNow, FiftyCount,
            InterfaceDetected: true, CaptureMethod: null);

        var rows = new List<Rectangle> { new(0, 50, 300, 20) };
        var prices = new Dictionary<string, PriceQuote?>
        {
            ["1x Unique Ring"] = new PriceQuote("0.5c - 5.0c - extra", 5.0m, true)
        };

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        _ = Assert.Single(result);
        Assert.True(result[0].Segments.Count >= 3);
    }

    [Fact]
    public void Build_RangeWithNoSpaceAroundDash_FallbackSingleSegment()
    {
        var snapshot = new LeagueWindowSnapshot(
            ChaosOrbItem, DateTimeOffset.UtcNow, SingleCount,
            InterfaceDetected: true, CaptureMethod: null);

        var rows = new List<Rectangle> { new(0, 100, 300, 20) };
        var prices = new Dictionary<string, PriceQuote?>
        {
            ["1x Chaos Orb"] = new PriceQuote("0.5c-5.0c", 5.0m, true) // No space before dash
        };

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        _ = Assert.Single(result);
        _ = Assert.Single(result[0].Segments); // Falls back — separator " -" not found
    }

    [Fact]
    public void Build_RangeWithLeadingWhitespace_Parsed()
    {
        var snapshot = new LeagueWindowSnapshot(
            UniqueRingItem, DateTimeOffset.UtcNow, FiftyCount,
            InterfaceDetected: true, CaptureMethod: null);

        var rows = new List<Rectangle> { new(0, 50, 300, 20) };
        var prices = new Dictionary<string, PriceQuote?>
        {
            ["1x Unique Ring"] = new PriceQuote("  0.5c - 5.0c", 5.0m, true)
        };

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        _ = Assert.Single(result);
        Assert.True(result[0].Segments.Count >= 3);
    }

    [Fact]
    public void Build_ExactNonRangeWithDivineLabel_ReturnsSingleSegment()
    {
        var snapshot = new LeagueWindowSnapshot(
            UniqueRingItem, DateTimeOffset.UtcNow, FiftyCount,
            InterfaceDetected: true, CaptureMethod: null);

        var rows = new List<Rectangle> { new(0, 50, 300, 20) };
        var prices = new Dictionary<string, PriceQuote?>
        {
            ["1x Unique Ring"] = new PriceQuote("2.5d", 2.5m, false)
        };

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        _ = Assert.Single(result);
    }

    [Fact]
    public void Build_MismatchedRowCount_OnlyMatchesAvailable()
    {
        var snapshot = new LeagueWindowSnapshot(
            ["1x A", "1x B", "1x C"],
            DateTimeOffset.UtcNow,
            [100, 100, 100],
            InterfaceDetected: true, CaptureMethod: null);

        // Only 2 rows for 3 items
        var rows = new List<Rectangle> { new(0, 0, 300, 20), new(0, 20, 300, 20) };
        var prices = new Dictionary<string, PriceQuote?>
        {
            ["1x A"] = new PriceQuote("1.0c", 1.0m, false),
            ["1x B"] = new PriceQuote("2.0c", 2.0m, false),
            ["1x C"] = new PriceQuote("3.0c", 3.0m, false)
        };

        var result = PriceRowLayout.Build(snapshot, prices, rows, TestOptions);
        Assert.Equal(2, result.Count); // Only first 2 matched
    }
}
