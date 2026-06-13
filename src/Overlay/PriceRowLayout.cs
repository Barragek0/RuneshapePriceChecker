using RuneshapePriceChecker.Configuration;
using RuneshapePriceChecker.Contracts;

namespace RuneshapePriceChecker.Overlay;

internal sealed record OverlayTextSegment(string Text, Color Color, float GlowStrength);
internal sealed record OverlayRowEntry(int RowY, int RowHeight, IReadOnlyList<OverlayTextSegment> Segments);

internal static class PriceRowLayout
{
    public static IReadOnlyList<OverlayRowEntry> Build(
        LeagueWindowSnapshot snapshot,
        IReadOnlyDictionary<string, PriceQuote?> pricesByItemName,
        IReadOnlyList<Rectangle> rows,
        PricingCacheOptions pricing)
    {
        var count = Math.Min(snapshot.ItemNames.Count, rows.Count);
        var entries = new List<OverlayRowEntry>(count);

        for (var i = 0; i < count; i++)
        {
            var itemName = snapshot.ItemNames[i];
            var row = rows[i];
            var quote = pricesByItemName.TryGetValue(itemName, out var value) ? value : null;
            if (quote is null)
                continue;

            var segments = BuildSegments(quote, pricing);
            entries.Add(new OverlayRowEntry(row.Y, row.Height, segments));
        }

        return entries;
    }

    private static IReadOnlyList<OverlayTextSegment> BuildSegments(PriceQuote quote, PricingCacheOptions pricing)
    {
        var fallbackColor = PriceColorCalculator.TryParseDisplayedChaosEquivalent(quote.Label, pricing, out var parsedDisplayValue)
            ? PriceColorCalculator.GetPriceColor(parsedDisplayValue, pricing)
            : PriceColorCalculator.GetPriceColor(quote.RepresentativeChaosValue, pricing);

        if (!quote.IsRange)
            return [new OverlayTextSegment(quote.Label, fallbackColor, PriceColorCalculator.GetDivineGlowStrength(quote.Label))];

        const string separator = " -";
        var splitIndex = quote.Label.IndexOf(separator, StringComparison.Ordinal);
        if (splitIndex < 0)
            return [new OverlayTextSegment(quote.Label, fallbackColor, PriceColorCalculator.GetDivineGlowStrength(quote.Label))];

        var leftText = quote.Label[..splitIndex];
        var rightText = quote.Label[(splitIndex + separator.Length)..];

        var leftColor = PriceColorCalculator.TryParseDisplayedChaosEquivalent(leftText, pricing, out var leftChaos)
            ? PriceColorCalculator.GetPriceColor(leftChaos, pricing)
            : fallbackColor;

        var rightColor = PriceColorCalculator.TryParseDisplayedChaosEquivalent(rightText, pricing, out var rightChaos)
            ? PriceColorCalculator.GetPriceColor(rightChaos, pricing)
            : fallbackColor;

        return
        [
            new OverlayTextSegment(leftText, leftColor, PriceColorCalculator.GetDivineGlowStrength(leftText)),
            new OverlayTextSegment(separator, Color.White, 0f),
            new OverlayTextSegment(rightText, rightColor, PriceColorCalculator.GetDivineGlowStrength(rightText))
        ];
    }
}
