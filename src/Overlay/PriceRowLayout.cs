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

    private static List<OverlayTextSegment> BuildSegments(PriceQuote quote, PricingCacheOptions pricing)
    {
        var fallbackColor = PriceColorCalculator.TryParseDisplayedChaosEquivalent(quote.Label, pricing, out var parsedDisplayValue)
            ? PriceColorCalculator.GetPriceColor(parsedDisplayValue, pricing)
            : PriceColorCalculator.GetPriceColor(quote.RepresentativeChaosValue, pricing);

        // Override color for low-volume items when match-color is enabled
        var iconColor = quote.VolumeLevel switch
        {
            VolumeLevel.VeryLow => Color.FromArgb(255, 255, 72, 72),   // red
            VolumeLevel.Low => Color.FromArgb(255, 255, 196, 54),       // yellow
            VolumeLevel.Normal => Color.Transparent,
            _ => Color.Transparent
        };

        if (quote.VolumeLevel != VolumeLevel.Normal && pricing.TradeVolumeMatchColor)
            fallbackColor = iconColor;

        if (!quote.IsRange)
        {
            if (quote.VolumeLevel != VolumeLevel.Normal)
                return [new OverlayTextSegment("\u26A0  ", iconColor, 0f), new OverlayTextSegment(quote.Label, fallbackColor, PriceColorCalculator.GetDivineGlowStrength(quote.Label))];
            return [new OverlayTextSegment(quote.Label, fallbackColor, PriceColorCalculator.GetDivineGlowStrength(quote.Label))];
        }

        const string separator = " -";
        var splitIndex = quote.Label.IndexOf(separator, StringComparison.Ordinal);
        if (splitIndex < 0)
        {
            if (quote.VolumeLevel != VolumeLevel.Normal)
                return [new OverlayTextSegment("\u26A0  ", iconColor, 0f), new OverlayTextSegment(quote.Label, fallbackColor, PriceColorCalculator.GetDivineGlowStrength(quote.Label))];
            return [new OverlayTextSegment(quote.Label, fallbackColor, PriceColorCalculator.GetDivineGlowStrength(quote.Label))];
        }

        var leftText = quote.Label[..splitIndex];
        var rightText = quote.Label[(splitIndex + separator.Length)..];

        var leftColor = PriceColorCalculator.TryParseDisplayedChaosEquivalent(leftText, pricing, out var leftChaos)
            ? PriceColorCalculator.GetPriceColor(leftChaos, pricing)
            : fallbackColor;

        var rightColor = PriceColorCalculator.TryParseDisplayedChaosEquivalent(rightText, pricing, out var rightChaos)
            ? PriceColorCalculator.GetPriceColor(rightChaos, pricing)
            : fallbackColor;

        var segments = new List<OverlayTextSegment>(4);
        if (quote.VolumeLevel != VolumeLevel.Normal)
            segments.Add(new OverlayTextSegment("\u26A0  ", iconColor, 0f));
        segments.Add(new OverlayTextSegment(leftText, leftColor, PriceColorCalculator.GetDivineGlowStrength(leftText)));
        segments.Add(new OverlayTextSegment(separator, Color.White, 0f));
        segments.Add(new OverlayTextSegment(rightText, rightColor, PriceColorCalculator.GetDivineGlowStrength(rightText)));
        return segments;
    }
}
