using System.Globalization;
using System.Text;

namespace RuneshapePriceChecker.Pricing;

internal static class FallbackProvider
{
    // Translation dictionary fallbacks (applied after exact/diacritics/apostrophe/tier)

    /// <summary>Adaptive fuzzy distance: larger strings get a higher allowance.</summary>
    public static int FuzzyMaxDist(string text) => Math.Max(3, Math.Min(5, text.Length / 5));

    // all languages — returns closest match within maxDist (lowest edit distance wins)
    public static string? TryMultiCharTranslation(string text, IEnumerable<KeyValuePair<string, string>> dictionary, int maxDist)
    {
        string? best = null;
        var bestDist = maxDist + 1;
        foreach (var kvp in dictionary)
        {
            var dist = StrComp.GetEditDistance(text, kvp.Key, maxDist);
            if (dist >= 0 && dist < bestDist)
            {
                bestDist = dist;
                best = kvp.Value;
                if (dist == 0) return best;
            }
        }
        return best;
    }

    // all languages — returns closest match within maxDist
    public static string? TrySpaceStrippedMultiCharTranslation(string text, string noSpaces,
        IEnumerable<KeyValuePair<string, string>> dictionary, int maxDist)
    {
        string? best = null;
        var bestDist = maxDist + 1;
        foreach (var kvp in dictionary)
        {
            var keyNoSpaces = RemoveAllSpaces(kvp.Key);
            var dist = StrComp.GetEditDistance(noSpaces, keyNoSpaces, maxDist);
            if (dist >= 0 && dist < bestDist)
            {
                bestDist = dist;
                best = kvp.Value;
                if (dist == 0) return best;
            }
        }
        return best;
    }

    // all languages — items only in ndjson with level suffix ("(Stufe 10)")
    public static string? TryLevelSuffixStrippedExact(string text, string noApos,
        IEnumerable<KeyValuePair<string, string>> dictionary)
    {
        foreach (var kvp in dictionary)
        {
            var strippedKey = StripLevelSuffixFromKey(kvp.Key);
            if (strippedKey is null) continue;

            if (string.Equals(text, strippedKey, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
            if (noApos != text && string.Equals(noApos, strippedKey, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return null;
    }

    // all languages — returns closest match within maxDist
    public static string? TryLevelSuffixStrippedMultiChar(string text, string noApos,
        IEnumerable<KeyValuePair<string, string>> dictionary, int maxDist)
    {
        string? best = null;
        var bestDist = maxDist + 1;
        foreach (var kvp in dictionary)
        {
            var strippedKey = StripLevelSuffixFromKey(kvp.Key);
            if (strippedKey is null) continue;

            var dist = StrComp.GetEditDistance(text, strippedKey, maxDist);
            if (dist >= 0 && dist < bestDist)
            {
                bestDist = dist;
                best = kvp.Value;
                if (dist == 0) return best;
            }
            if (noApos != text)
            {
                dist = StrComp.GetEditDistance(noApos, strippedKey, maxDist);
                if (dist >= 0 && dist < bestDist)
                {
                    bestDist = dist;
                    best = kvp.Value;
                    if (dist == 0) return best;
                }
            }
        }
        return best;
    }

    // Cross-language / bundled translation fallback (non-English languages only)
    // Returns closest match within maxDist (lowest edit distance wins)
    public static string? TryCrossLanguageMultiCharMatch(string plainSearch, string noApos,
        IReadOnlyDictionary<string, string> bundledTranslations,
        string langCode, string? normalizedCode, int maxDist)
    {
        if (string.IsNullOrEmpty(plainSearch)) return null;

        string? best = null;
        var bestDist = maxDist + 1;
        foreach (var kvp in bundledTranslations)
        {
            if (!kvp.Key.EndsWith($"##{langCode}", StringComparison.OrdinalIgnoreCase) &&
                (normalizedCode is null || !kvp.Key.EndsWith($"##{normalizedCode}", StringComparison.OrdinalIgnoreCase)))
                continue;

            var keyName = kvp.Key[..kvp.Key.LastIndexOf("##", StringComparison.OrdinalIgnoreCase)];
            var dist = StrComp.GetEditDistance(plainSearch, keyName, maxDist);
            if (dist >= 0 && dist < bestDist)
            {
                bestDist = dist;
                best = kvp.Value;
                if (dist == 0) return best;
            }
            if (noApos != plainSearch)
            {
                dist = StrComp.GetEditDistance(noApos, keyName, maxDist);
                if (dist >= 0 && dist < bestDist)
                {
                    bestDist = dist;
                    best = kvp.Value;
                    if (dist == 0) return best;
                }
            }
        }
        return best;
    }

    // Pricing lookup fallbacks — language-agnostic (operates on English names)
    public static bool TryResolveSingleLetterOffCandidate(IReadOnlyList<string> keys,
        IEnumerable<string> knownKeys, out string correctedKey)
    {
        correctedKey = string.Empty;
        var matchCount = 0;

        foreach (var key in keys)
        {
            if (key.Length < 7) continue;

            foreach (var known in knownKeys)
            {
                if (!IsSingleSubstitutionAway(key, known)) continue;

                correctedKey = known;
                matchCount++;
                if (matchCount > 1)
                {
                    correctedKey = string.Empty;
                    return false;
                }
            }
        }

        return matchCount == 1;
    }

    public static string? ResolveFewCharsAwayCandidate(IReadOnlyList<string> keys,
        IEnumerable<string> knownKeys)
    {
        foreach (var key in keys)
        {
            if (key.Length < 7) continue;

            string? best = null;
            foreach (var known in knownKeys)
            {
                if (!StrComp.AreFewCharsAway(key, known, 2)) continue;
                if (best is not null) return null; // ambiguous
                best = known;
            }
            if (best is not null) return best;
        }
        return null;
    }

    // Internal helpers

    private static bool IsSingleSubstitutionAway(string source, string candidate)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate))
            return false;

        var lenDiff = source.Length - candidate.Length;
        if (lenDiff == 0)
        {
            var differences = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == candidate[i]) continue;
                differences++;
                if (differences > 1) return false;
            }
            return differences == 1;
        }

        if (lenDiff is 1 or -1)
        {
            var longer = lenDiff == 1 ? source : candidate;
            var shorter = lenDiff == 1 ? candidate : source;

            var differences = 0;
            var si = 0;
            for (var li = 0; li < longer.Length && si < shorter.Length; li++, si++)
            {
                if (longer[li] == shorter[si]) continue;
                differences++;
                if (differences > 1) return false;
                si--;
            }
            return differences <= 1;
        }

        return false;
    }

    internal static string RemoveApostrophes(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\'') < 0)
            return text;
        return text.Replace('\'', ' ');
    }

    internal static string RemoveAllSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        for (var i = 0; i < text.Length; i++)
            if (char.IsWhiteSpace(text[i]))
                goto needsStrip;
        return text;
    needsStrip:
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsWhiteSpace(c))
                _ = sb.Append(c);
        return sb.ToString();
    }

    internal static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(text.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                _ = sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static string? StripLevelSuffixFromKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var lastParen = key.LastIndexOf('(');
        if (lastParen < 0) return null;
        var suffix = key.AsSpan(lastParen);
        var closeParen = suffix.LastIndexOf(')');
        if (closeParen < 0 || closeParen != suffix.Length - 1) return null;
        var inner = suffix[1..^1].Trim();
        var spaceIdx = inner.LastIndexOf(' ');
        if (spaceIdx < 0) return null;
        var word = inner[..spaceIdx];
        var num = inner[(spaceIdx + 1)..];
        if (word.Length > 0 && int.TryParse(num, out _))
        {
            var stripped = key.AsSpan(0, lastParen).TrimEnd();
            return stripped.Length > 0 ? stripped.ToString() : null;
        }
        return null;
    }
}
