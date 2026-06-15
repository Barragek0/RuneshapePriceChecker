namespace RuneshapePriceChecker;

internal static class StrComp
{
    public const StringComparison Ordinal = StringComparison.Ordinal;
    public const StringComparison OrdinalIgnoreCase = StringComparison.OrdinalIgnoreCase;

    public static bool IsOneCharAway(ReadOnlySpan<char> source, ReadOnlySpan<char> target)
    {
        var lenDiff = source.Length - target.Length;
        if (lenDiff is < -1 or > 1) return false;

        if (lenDiff == 0)
        {
            var diffs = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (char.ToUpperInvariant(source[i]) != char.ToUpperInvariant(target[i])) diffs++;
                if (diffs > 1) return false;
            }
            return diffs == 1;
        }

        var longer = lenDiff == 1 ? source : target;
        var shorter = lenDiff == 1 ? target : source;
        var d = 0;
        var si = 0;
        for (var li = 0; li < longer.Length && si < shorter.Length; li++, si++)
        {
            if (char.ToUpperInvariant(longer[li]) == char.ToUpperInvariant(shorter[si])) continue;
            d++;
            if (d > 1) return false;
            si--;
        }
        return d <= 1;
    }

    public static bool IsTwoCharsAway(ReadOnlySpan<char> source, ReadOnlySpan<char> target)
    {
        if (source.Length <= 6 || target.Length <= 6) return false;

        var lenDiff = source.Length - target.Length;
        if (lenDiff is < -2 or > 2) return false;

        if (lenDiff == 0)
        {
            var diffs = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (char.ToUpperInvariant(source[i]) != char.ToUpperInvariant(target[i])) diffs++;
                if (diffs > 2) return false;
            }
            return diffs is 1 or 2;
        }

        var longer = lenDiff > 0 ? source : target;
        var shorter = lenDiff > 0 ? target : source;
        var d = 0;
        var si = 0;
        for (var li = 0; li < longer.Length && si < shorter.Length; li++, si++)
        {
            if (char.ToUpperInvariant(longer[li]) == char.ToUpperInvariant(shorter[si])) continue;
            d++;
            if (d > 2) return false;
            si--;
        }
        d += longer.Length - Math.Min(longer.Length, shorter.Length + d);
        return d <= 2;
    }
}
