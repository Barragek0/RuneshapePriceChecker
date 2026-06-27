using System.Globalization;
using RuneshapePriceChecker.Configuration;

namespace RuneshapePriceChecker.Overlay;

internal static class PriceColorCalculator
{
    public static Color GetPriceColor(decimal chaosValue, PricingCacheOptions pricing)
    {
        if (chaosValue < 0m)
            return Color.FromArgb(255, 220, 60, 60);

        var chaos = Math.Max(0m, chaosValue);
        var redThreshold = pricing.RedThreshold;
        var orangeThreshold = pricing.OrangeThreshold;
        var greenThreshold = pricing.GreenThreshold;

        var red = Color.FromArgb(255, 255, 72, 72);
        var orange = Color.FromArgb(255, 255, 196, 54);
        var green = Color.FromArgb(255, 88, 255, 122);

        if (chaos <= redThreshold)
            return red;

        if (chaos < orangeThreshold)
        {
            var t = (double)((chaos - redThreshold) / (orangeThreshold - redThreshold));
            return LerpColor(red, orange, t);
        }

        if (chaos < greenThreshold)
        {
            var t = (double)((chaos - orangeThreshold) / (greenThreshold - orangeThreshold));
            return LerpColor(orange, green, t);
        }

        return green;
    }

    public static float GetDivineGlowStrength(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0f;

        var trimmed = text.Trim();
        if (!trimmed.EndsWith('d'))
            return 0f;

        var numericPart = trimmed[..^1];
        if (!decimal.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var divineValue))
            return 0f;

        if (divineValue <= 0m)
            return 0f;

        var clamped = decimal.Min(100m, decimal.Max(1m, divineValue));
        var normalized = (float)((clamped - 1m) / 99m);
        return 0.62f + (normalized * 0.35f);
    }

    public static bool TryParseDisplayedChaosEquivalent(string formattedAmount, PricingCacheOptions pricing, out decimal chaosEquivalent)
    {
        chaosEquivalent = 0m;
        if (string.IsNullOrWhiteSpace(formattedAmount))
            return false;

        var trimmed = formattedAmount.Trim();

        if (trimmed.EndsWith("ex", StringComparison.OrdinalIgnoreCase))
        {
            var valueText = trimmed[..^2].Trim();
            if (valueText.StartsWith('<'))
                valueText = valueText[1..];

            if (decimal.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var exaltValue))
            {
                chaosEquivalent = Math.Max(0m, exaltValue);
                return true;
            }
            return false;
        }

        if (trimmed.EndsWith('c'))
        {
            var valueText = trimmed[..^1].Trim();
            if (valueText.StartsWith('<'))
                valueText = valueText[1..];

            if (decimal.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var chaosValue))
            {
                chaosEquivalent = Math.Max(0m, chaosValue);
                return true;
            }
            return false;
        }

        if (trimmed.EndsWith('d'))
        {
            var valueText = trimmed[..^1].Trim();
            if (valueText.StartsWith('<'))
                valueText = valueText[1..];

            if (decimal.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var divineValue))
            {
                chaosEquivalent = Math.Max(pricing.GreenThreshold, Math.Max(0m, divineValue));
                return true;
            }
            return false;
        }

        return false;
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0d, 1d);
        var r = (int)Math.Round(a.R + ((b.R - a.R) * t));
        var g = (int)Math.Round(a.G + ((b.G - a.G) * t));
        var bl = (int)Math.Round(a.B + ((b.B - a.B) * t));
        return Color.FromArgb(255, r, g, bl);
    }
}
