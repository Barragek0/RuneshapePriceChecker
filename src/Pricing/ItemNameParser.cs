using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuneshapePriceChecker.Pricing;

public static class ItemNameParser
{
    private static readonly Regex MultiWhitespace = StrComp.MultiWhitespace;
    private static readonly Regex QuantityPrefixWithX = new("^(?<quantity>\\d+|[AaIiLlTt|Oo0])\\s*[xX]\\s+(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex QuantityPrefixWithoutX = new("^(?<quantity>\\d+|[IiLl|Oo0])\\s+(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex LeadingQuantityEchoToken = new("^(?:[xX]+(?:\\s+[xX]+)*\\s+)(?<name>.+)$", RegexOptions.Compiled);
    private static readonly Regex IsolatedImToken = new("(?<=\\s)im(?=\\s)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TieredOrb = new("^(?:GREATER|PERFECT)\\s+(ORB OF .+)$", RegexOptions.Compiled);
    private static readonly Regex TieredRune = new("^(?:GREATER|PERFECT)\\s+(.+\\s+RUNE)$", RegexOptions.Compiled);
    private static readonly Regex LevelSuffix = new(@"\s+\S+\s*\d+[)\]]?\s*$", RegexOptions.Compiled);
    private static readonly Regex QuantitySuffixWithX = new(@"^(?<name>.+)\s[xX]\s*(?<quantity>\d+|[AaIiLlTt|Oo0])\s*$", RegexOptions.Compiled);
    private static readonly Regex TrailingQuantityNumber = new(@"^(?<name>.+)\s+(?<quantity>\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex KoreanQuantitySuffix = new(@"^(?<name>.+?)\s+(?<quantity>\d+)\s*개\s*$", RegexOptions.Compiled);
    private static readonly Regex RussianQuantityPrefix = new(@"^(?<quantity>\d+)\s*шт\s+(?<name>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, int> OcrQuantityTokenMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1,
        ["i"] = 1,
        ["l"] = 1,
        ["t"] = 1,
        ["|"] = 1,
        ["o"] = 2,
        ["0"] = 2
    };

    private static readonly Dictionary<string, string> NormalizedWordSwaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0RB"] = "ORB",
        ["GRB"] = "ORB",
        ["CEM"] = "GEM",
        ["LRON"] = "IRON"
    };

    private static readonly Dictionary<string, string[]> IdAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gcp"] = ["Gemcutter's Prism"],
        ["bauble"] = ["Glassblower's Bauble"],
        ["etcher"] = ["Arcanist's Etcher"],
        ["aug"] = ["Orb of Augmentation"],
        ["alch"] = ["Orb of Alchemy"],
        ["transmute"] = ["Orb of Transmutation"],
        ["regal"] = ["Regal Orb"],
        ["chaos"] = ["Chaos Orb"],
        ["divine"] = ["Divine Orb"],
        ["exalted"] = ["Exalted Orb"],
        ["annul"] = ["Orb of Annulment"],
        ["artificers"] = ["Artificer's Orb"],
        ["chance"] = ["Orb of Chance"],
        ["mirror"] = ["Mirror of Kalandra"],
        ["scrap"] = ["Armourer's Scrap"],
        ["vaal"] = ["Vaal Orb"],
        ["whetstone"] = ["Blacksmith's Whetstone"],
        ["wisdom"] = ["Scroll of Wisdom"]
    };

    // Cross-language base-type keywords loaded from ocr/unique-category-map.json.
    // Maps foreign words (e.g. "ДВУРУЧНАЯ БУЛАВА", "RING") to their English
    // UNIQUE category (e.g. "WEAPON", "HELMET"). The lookup is language-agnostic:
    // any word in the item name that matches a key will map to the category.
    private static readonly Lazy<Dictionary<string, string>> BaseTypeKeywords = new(LoadBaseTypeKeywords);

    private static Dictionary<string, string> LoadBaseTypeKeywords()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("unique-category-map.json", StringComparison.OrdinalIgnoreCase));

            if (name is not null)
            {
                using var stream = asm.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream!);
                return ParseCategoryMap(reader.ReadToEnd());
            }

            // Development fallback: file on disk relative to the project
            var projectDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
            var filePath = Path.Combine(projectDir, "ocr", "unique-category-map.json");
            if (File.Exists(filePath))
                return ParseCategoryMap(File.ReadAllText(filePath));
        }
        catch
        {
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseCategoryMap(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
        if (raw is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, keywords) in raw)
            foreach (var word in keywords)
                if (!string.IsNullOrWhiteSpace(word) && !map.ContainsKey(word))
                    map[word] = category;
        return map;
    }

    public static readonly string[] UncutGemFamilies =
    [
        "UNCUT SUPPORT GEM",
        "UNCUT SKILL GEM",
        "UNCUT SPIRIT GEM"
    ];

    public static ParsedDetectedItem ParseDetectedItem(string raw, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ParsedDetectedItem(string.Empty, 1);

        var cleanedRaw = MultiWhitespace.Replace(IsolatedImToken.Replace(raw, string.Empty), " ").Trim();
        if (string.IsNullOrWhiteSpace(cleanedRaw))
            return new ParsedDetectedItem(string.Empty, 1);

        // Try prefix quantity: "1x Name", "I Name", "O Name"
        var match = QuantityPrefixWithX.Match(cleanedRaw);

        // Russian "шт" prefix
        if (!match.Success)
            match = RussianQuantityPrefix.Match(cleanedRaw);

        if (!match.Success)
            match = QuantityPrefixWithoutX.Match(cleanedRaw);

        if (match.Success)
        {
            var quantity = NormalizeQuantityToken(match.Groups["quantity"].Value);
            var name = TrimLeadingEcho(match.Groups["name"].Value.Trim());
            if (string.IsNullOrWhiteSpace(name))
                return new ParsedDetectedItem(cleanedRaw, 1);
            var (stripped, level) = StripLevelSuffix(name);
            return new ParsedDetectedItem(stripped, quantity, level);
        }

        // Try suffix quantity: "Name x1" (Spanish)
        var suffixMatch = QuantitySuffixWithX.Match(cleanedRaw);
        if (suffixMatch.Success)
        {
            var namePart = suffixMatch.Groups["name"].Value.Trim();
            var quantity = NormalizeQuantityToken(suffixMatch.Groups["quantity"].Value);
            var (stripped, level) = StripLevelSuffix(namePart);
            return new ParsedDetectedItem(stripped, quantity, level);
        }

        // Russian trailing number: "Предмет 1"
        if (string.Equals(language, "rus", StringComparison.OrdinalIgnoreCase))
        {
            var trailingMatch = TrailingQuantityNumber.Match(cleanedRaw);
            if (trailingMatch.Success)
            {
                var namePart = trailingMatch.Groups["name"].Value.Trim();
                var quantity = NormalizeQuantityToken(trailingMatch.Groups["quantity"].Value);
                var (stripped, level) = StripLevelSuffix(namePart);
                return new ParsedDetectedItem(stripped, quantity, level);
            }
        }

        // Korean trailing quantity: "이름 5개"
        var koreanMatch = KoreanQuantitySuffix.Match(cleanedRaw);
        if (koreanMatch.Success)
        {
            var namePart = koreanMatch.Groups["name"].Value.Trim();
            var quantity = NormalizeQuantityToken(koreanMatch.Groups["quantity"].Value);
            var (stripped, level) = StripLevelSuffix(namePart);
            return new ParsedDetectedItem(stripped, quantity, level);
        }

        var (n, l) = StripLevelSuffix(cleanedRaw);
        return new ParsedDetectedItem(n, 1, l);
    }

    private static (string Name, int Level) StripLevelSuffix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (text, 0);

        var match = LevelSuffix.Match(text);
        if (!match.Success)
            return (text, 0);

        var suffix = match.Value.Trim(); // e.g. "Niveau 19", "Level 19", "Stufe 19"
        var parts = suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[^1].TrimEnd(')'), out var level))
        {
            // If the word before the number is just "x" or "X", it's a quantity
            // separator (e.g. Spanish "x1"), not a level word.
            if (parts[^2].Length == 1 && (parts[^2][0] is 'x' or 'X'))
                return (text, 0);

            var name = LevelSuffix.Replace(text, string.Empty).Trim();
            return (name, level);
        }

        return (text, 0);
    }

    public static int NormalizeQuantityToken(string rawQuantity)
    {
        if (string.IsNullOrWhiteSpace(rawQuantity))
            return 1;

        if (int.TryParse(rawQuantity, out var parsed) && parsed > 0)
            return parsed;

        return OcrQuantityTokenMap.TryGetValue(rawQuantity.Trim(), out var mapped) ? mapped : 1;
    }

    public static string ApplyNormalizedWordSwaps(string normalizedUpperText)
    {
        if (string.IsNullOrWhiteSpace(normalizedUpperText))
            return string.Empty;
        var parts = normalizedUpperText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            if (NormalizedWordSwaps.TryGetValue(parts[i], out var replacement))
                parts[i] = replacement;
        return string.Join(' ', parts);
    }

    public static IEnumerable<string> ExpandIdAliases(string id)
    {
        if (!string.IsNullOrWhiteSpace(id) && IdAliases.TryGetValue(id, out var aliases))
        {
            foreach (var alias in aliases)
                if (!string.IsNullOrWhiteSpace(alias))
                    yield return alias;
        }
    }

    public static bool TryGetTierFallbackKey(string normalizedItemName, out string fallbackKey)
    {
        fallbackKey = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedItemName))
            return false;

        var orbMatch = TieredOrb.Match(normalizedItemName);
        if (orbMatch.Success)
        {
            fallbackKey = orbMatch.Groups[1].Value;
            return true;
        }

        var runeMatch = TieredRune.Match(normalizedItemName);
        if (runeMatch.Success)
        {
            fallbackKey = runeMatch.Groups[1].Value;
            return true;
        }

        return false;
    }

    public static IEnumerable<string> BuildUniqueCategoryLookupCandidates(string normalizedItemName)
    {
        if (string.IsNullOrWhiteSpace(normalizedItemName))
            yield break;

        if (normalizedItemName.StartsWith("UNIQUE ", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in BuildFromEnglishUniqueTail(normalizedItemName["UNIQUE ".Length..].Trim()))
                yield return c;
            yield break;
        }

        var nameWords = normalizedItemName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var kvp in BaseTypeKeywords.Value)
        {
            // Word-boundary match — prevents keywords like "RING" matching inside
            // "SCOURING" or "BOW" matching inside "RAINBOW" or "ELBOW".
            var keyWords = kvp.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var match = keyWords.Length == 1
                ? nameWords.Any(w => string.Equals(w, kvp.Key, StringComparison.OrdinalIgnoreCase))
                : nameWords.Length >= keyWords.Length && HasConsecutiveWords(nameWords, keyWords);
            if (match)
            {
                foreach (var c in BuildFromEnglishUniqueTail(kvp.Value))
                    yield return c;
                yield break;
            }
        }
    }

    private static IEnumerable<string> BuildFromEnglishUniqueTail(string categoryTail)
    {
        if (string.IsNullOrWhiteSpace(categoryTail))
            yield break;

        var singularTail = categoryTail.EndsWith("S", StringComparison.OrdinalIgnoreCase)
            ? categoryTail[..^1]
            : categoryTail;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"UNIQUE {categoryTail}",
            $"UNIQUE {singularTail}",
            $"UNIQUE {categoryTail.Replace("ARMOR", "ARMOUR", StringComparison.OrdinalIgnoreCase)}",
            $"UNIQUE {categoryTail.Replace("ARMOUR", "ARMOR", StringComparison.OrdinalIgnoreCase)}",
            $"UNIQUE {categoryTail.Replace("JEWELRY", "JEWELLERY", StringComparison.OrdinalIgnoreCase)}",
            $"UNIQUE {categoryTail.Replace("JEWELLERY", "JEWELRY", StringComparison.OrdinalIgnoreCase)}",
        };

        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c))
                yield return c;
    }

    private static bool HasConsecutiveWords(string[] nameWords, string[] keyWords)
    {
        if (keyWords.Length > nameWords.Length) return false;
        for (var i = 0; i <= nameWords.Length - keyWords.Length; i++)
        {
            var match = true;
            for (var j = 0; j < keyWords.Length; j++)
            {
                if (!string.Equals(nameWords[i + j], keyWords[j], StringComparison.OrdinalIgnoreCase))
                { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    public static string FormatAmount(
        decimal chaosValue,
        decimal divineOrbChaosValue,
        decimal exaltedOrbChaosValue,
        string? displayCurrency)
    {
        var chaos = Math.Max(0m, chaosValue);

        var useExaltedDisplay =
            string.Equals(displayCurrency, "exalt", StringComparison.OrdinalIgnoreCase) &&
            exaltedOrbChaosValue > 0m;

        if (useExaltedDisplay)
        {
            if (divineOrbChaosValue > 0m && chaos >= divineOrbChaosValue)
            {
                var divine = TruncateToSingleDecimal(chaos / divineOrbChaosValue);
                return $"{divine.ToString("0.#", CultureInfo.InvariantCulture)}d";
            }

            var exalted = TruncateToSingleDecimal(chaos / exaltedOrbChaosValue);
            if (chaos > 0m && exalted <= 0m)
            {
                return "<0.1ex";
            }

            return $"{exalted.ToString("0.#", CultureInfo.InvariantCulture)}ex";
        }

        if (divineOrbChaosValue > 0m && chaos >= divineOrbChaosValue)
        {
            var divine = TruncateToSingleDecimal(chaos / divineOrbChaosValue);
            return $"{divine.ToString("0.#", CultureInfo.InvariantCulture)}d";
        }

        var truncatedChaos = TruncateToSingleDecimal(chaos);
        if (chaos > 0m && truncatedChaos <= 0m)
        {
            return "<0.1c";
        }

        return $"{truncatedChaos.ToString("0.#", CultureInfo.InvariantCulture)}c";
    }

    private static decimal TruncateToSingleDecimal(decimal value)
    {
        return Math.Truncate(value * 10m) / 10m;
    }

    private static string TrimLeadingEcho(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        var match = LeadingQuantityEchoToken.Match(name);
        return match.Success ? match.Groups["name"].Value.Trim() : name;
    }

    internal static readonly string[] UniqueCategoryKeywords =
    [
        "Two Hand Mace", "One Hand Mace", "Quarterstaff", "Crossbow",
        "Body Armour", "Talisman", "Sceptre", "Quiver", "Shield",
        "Helmet", "Gloves", "Boots", "Jewellery", "Jewelry",
        "Amulet", "Spear", "Staff", "Focus", "Ring", "Belt",
        "Wand", "Bow",
    ];

    internal static readonly HashSet<string> WeaponCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "WAND", "TWO HAND MACE", "ONE HAND MACE", "QUARTERSTAFF", "CROSSBOW",
        "TALISMAN", "SCEPTRE", "QUIVER", "SHIELD", "SPEAR", "STAFF", "FOCUS", "BOW",
    };

    internal static readonly HashSet<string> ArmourCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "BODY ARMOUR", "HELMET", "GLOVES", "BOOTS",
    };

    internal static readonly HashSet<string> AccessoryCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "RING", "AMULET", "BELT", "JEWELLERY", "JEWELRY",
    };

    internal static string? TryGetUniqueCategory(string itemName)
    {
        foreach (var keyword in UniqueCategoryKeywords)
        {
            if (itemName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return keyword;
        }

        return UniqueItemTypeLookup.TryGetCategory(itemName);
    }
}

public readonly record struct ParsedDetectedItem(string Name, int Quantity, int Level = 0);