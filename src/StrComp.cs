using System.Text.RegularExpressions;

namespace RuneshapePriceChecker;

internal static class StrComp
{
    public static readonly Regex MultiWhitespace = new(@"\s+", RegexOptions.Compiled);

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

    public static bool AreFewCharsAway(ReadOnlySpan<char> source, ReadOnlySpan<char> target, int maxDistance) =>
        GetEditDistance(source, target, maxDistance) >= 0;

    public static int GetEditDistance(ReadOnlySpan<char> source, ReadOnlySpan<char> target, int maxDistance)
    {
        if (maxDistance < 1) return -1;

        var absLenDiff = Math.Abs(source.Length - target.Length);
        if (absLenDiff > maxDistance) return -1;

        if (source.Length == 0) return target.Length <= maxDistance ? target.Length : -1;
        if (target.Length == 0) return source.Length <= maxDistance ? source.Length : -1;

        ReadOnlySpan<char> a, b;
        if (source.Length <= target.Length) { a = source; b = target; }
        else { a = target; b = source; }

        var m = a.Length;
        var n = b.Length;

        Span<int> prev = stackalloc int[m + 1];
        Span<int> curr = stackalloc int[m + 1];

        for (var j = 0; j <= m; j++) prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            var best = curr[0];
            for (var j = 1; j <= m; j++)
            {
                var cost = a[j - 1] == b[i - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (curr[j] < best) best = curr[j];
            }
            if (best > maxDistance) return -1;
            var temp = prev; prev = curr; curr = temp;
        }
        var distance = prev[m];
        return distance <= maxDistance ? distance : -1;
    }
}
