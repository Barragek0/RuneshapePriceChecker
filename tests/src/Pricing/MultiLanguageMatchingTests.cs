using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RuneshapePriceChecker.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace RuneshapePriceChecker.Tests.Pricing;

public sealed class MultiLanguageMatchingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static TranslationCache CreateCache(string lang, string ndjson)
    {
        var ocrDir = Path.Combine(Path.GetTempPath(), "RPC-Test", Guid.NewGuid().ToString());
        var cache = new TranslationCache(new HttpClient(), NullLogger<TranslationCache>.Instance, ocrDir);
        cache.LoadFromString(lang, ndjson);
        return cache;
    }
    // French (ndjson format: one JSON object per line)
    public static readonly string FrNdjson = """
{"name":"Orbe du Chaos","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"Orbe Divin","refName":"Divine Orb","namespace":"ITEM"}
{"name":"Orbe Exalté","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"Miroir de Kalandra","refName":"Mirror of Kalandra","namespace":"ITEM"}
{"name":"Orbe d'Alchimie","refName":"Orb of Alchemy","namespace":"ITEM"}
{"name":"Orbe d'Augmentation","refName":"Orb of Augmentation","namespace":"ITEM"}
{"name":"Bulle de Souffleur de Verre","refName":"Glassblower's Bauble","namespace":"ITEM"}
{"name":"Prisme de Lapidaire","refName":"Gemcutter's Prism","namespace":"ITEM"}
{"name":"Ferraille d'Armurier","refName":"Armourer's Scrap","namespace":"ITEM"}
{"name":"Pierre à Aiguiser de Forgeron","refName":"Blacksmith's Whetstone","namespace":"ITEM"}
{"name":"Gemme de Compétence Brute","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Gemme de Soutien Brute","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Gemme d'Esprit Brute","refName":"Uncut Spirit Gem","namespace":"ITEM"}
{"name":"Rune de la Sauvagerie de Thane Girt","refName":"Thane Girt's Rune of Wildness","namespace":"ITEM"}
{"name":"Rune de la Maîtrise de Thane Grannell","refName":"Thane Grannell's Rune of Mastery","namespace":"ITEM"}
{"name":"Rune du Printemps de Thane Leld","refName":"Thane Leld's Rune of Spring","namespace":"ITEM"}
{"name":"Rune de l'Été de Thane Myrk","refName":"Thane Myrk's Rune of Summer","namespace":"ITEM"}
""";
    // German
    public static readonly string DeNdjson = """
{"name":"Chaosorb","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"Göttlicher Orb","refName":"Divine Orb","namespace":"ITEM"}
{"name":"Erhabener Orb","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"Spiegel von Kalandra","refName":"Mirror of Kalandra","namespace":"ITEM"}
{"name":"Orb der Alchemie","refName":"Orb of Alchemy","namespace":"ITEM"}
{"name":"Orb der Vergrößerung","refName":"Orb of Augmentation","namespace":"ITEM"}
{"name":"Glasbläser's Köder","refName":"Glassblower's Bauble","namespace":"ITEM"}
{"name":"Edelsteinschleifer's Prisma","refName":"Gemcutter's Prism","namespace":"ITEM"}
{"name":"Rüstungsschrott","refName":"Armourer's Scrap","namespace":"ITEM"}
{"name":"Schmiedewetzstein","refName":"Blacksmith's Whetstone","namespace":"ITEM"}
{"name":"Roher Fertigkeitsedelstein","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Roher Unterstützungsedelstein","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Roher Geistesedelstein","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";
    // Spanish
    public static readonly string EsNdjson = """
{"name":"Orbe del Caos","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"Orbe Divino","refName":"Divine Orb","namespace":"ITEM"}
{"name":"Orbe Exaltado","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"Espejo de Kalandra","refName":"Mirror of Kalandra","namespace":"ITEM"}
{"name":"Orbe de Alquimia","refName":"Orb of Alchemy","namespace":"ITEM"}
{"name":"Orbe de Aumento","refName":"Orb of Augmentation","namespace":"ITEM"}
{"name":"Cebo de Soplador de Vidrio","refName":"Glassblower's Bauble","namespace":"ITEM"}
{"name":"Prisma de Lapidario","refName":"Gemcutter's Prism","namespace":"ITEM"}
{"name":"Chatarra de Armero","refName":"Armourer's Scrap","namespace":"ITEM"}
{"name":"Piedra de Afilar de Herrero","refName":"Blacksmith's Whetstone","namespace":"ITEM"}
{"name":"Gema de Habilidad Bruta","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Gema de Apoyo Bruta","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Gema de Espíritu Bruta","refName":"Uncut Spirit Gem","namespace":"ITEM"}
{"name":"Runa de lo Salvaje de Thane Girt","refName":"Thane Girt's Rune of Wildness","namespace":"ITEM"}
{"name":"Runa de Maestría de Thane Grannell","refName":"Thane Grannell's Rune of Mastery","namespace":"ITEM"}
{"name":"Runa de la Primavera de Thane Leld","refName":"Thane Leld's Rune of Spring","namespace":"ITEM"}
{"name":"Runa del Verano de Thane Myrk","refName":"Thane Myrk's Rune of Summer","namespace":"ITEM"}
""";
    // Portuguese
    public static readonly string PtNdjson = """
{"name":"Orbe do Caos","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"Orbe Divino","refName":"Divine Orb","namespace":"ITEM"}
{"name":"Orbe Exaltado","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"Espelho de Kalandra","refName":"Mirror of Kalandra","namespace":"ITEM"}
{"name":"Orbe da Alquimia","refName":"Orb of Alchemy","namespace":"ITEM"}
{"name":"Orbe de Aumento","refName":"Orb of Augmentation","namespace":"ITEM"}
{"name":"Patuá de Soprador de Vidro","refName":"Glassblower's Bauble","namespace":"ITEM"}
{"name":"Prisma de Lapidário","refName":"Gemcutter's Prism","namespace":"ITEM"}
{"name":"Sucata de Armoeiro","refName":"Armourer's Scrap","namespace":"ITEM"}
{"name":"Pedra de Amolar de Ferreiro","refName":"Blacksmith's Whetstone","namespace":"ITEM"}
{"name":"Gema de Habilidade Bruta","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Gema de Suporte Bruta","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Gema de Espírito Bruta","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";
    // Russian
    public static readonly string RuNdjson = """
{"name":"Хаос","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"Божественный сфера","refName":"Divine Orb","namespace":"ITEM"}
{"name":"Возвышенный сфера","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"Зеркало Каландры","refName":"Mirror of Kalandra","namespace":"ITEM"}
{"name":"Сфера алхимии","refName":"Orb of Alchemy","namespace":"ITEM"}
{"name":"Сфера расширения","refName":"Orb of Augmentation","namespace":"ITEM"}
{"name":"Приманка стеклодува","refName":"Glassblower's Bauble","namespace":"ITEM"}
{"name":"Призма гранильщика","refName":"Gemcutter's Prism","namespace":"ITEM"}
{"name":"Лом бронника","refName":"Armourer's Scrap","namespace":"ITEM"}
{"name":"Точильный камень кузнеца","refName":"Blacksmith's Whetstone","namespace":"ITEM"}
{"name":"Неогранённый самоцвет умений","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Неогранённый самоцвет поддержки","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Неогранённый самоцвет духа","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";
    // Japanese
    public static readonly string JaNdjson = """
{"name":"カオスオーブ","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"神のオーブ","refName":"Divine Orb","namespace":"ITEM"}
{"name":"高貴なオーブ","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"カランドラの鏡","refName":"Mirror of Kalandra","namespace":"ITEM"}
{"name":"錬金術のオーブ","refName":"Orb of Alchemy","namespace":"ITEM"}
{"name":"スキルジェムの原石","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"サポートジェムの原石","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"スピリットジェムの原石","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";
    // Korean
    public static readonly string KoNdjson = """
{"name":"카오스 오브","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"신성한 오브","refName":"Divine Orb","namespace":"ITEM"}
{"name":"엑잘티드 오브","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"칼란드라의 거울","refName":"Mirror of Kalandra","namespace":"ITEM"}
{"name":"연금술의 오브","refName":"Orb of Alchemy","namespace":"ITEM"}
{"name":"미가공 스킬 젬","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"미가공 보조 젬","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"미가공 정신력 젬","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";
    // Chinese Traditional
    public static readonly string ZhNdjson = """
{"name":"混沌石","refName":"Chaos Orb","namespace":"ITEM"}
{"name":"神聖石","refName":"Divine Orb","namespace":"ITEM"}
{"name":"崇高石","refName":"Exalted Orb","namespace":"ITEM"}
{"name":"卡蘭德的魔鏡","refName":"Mirror of Kalandra","namespace":"ITEM"}
{"name":"點金石","refName":"Orb of Alchemy","namespace":"ITEM"}
{"name":"未切割的技能寶石","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"未切割的輔助寶石","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"精魂寶石","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

    public static IEnumerable<object[]> ExactMatchData()
    {
        // French - exact matches
        yield return ["fra", "FR exact Chaos", "Orbe du Chaos", "Chaos Orb"];
        yield return ["fra", "FR exact Divine", "Orbe Divin", "Divine Orb"];
        yield return ["fra", "FR exact Exalté", "Orbe Exalté", "Exalted Orb"];
        yield return ["fra", "FR exact Alchemy", "Orbe d'Alchimie", "Orb of Alchemy"];
        yield return ["fra", "FR exact Augment", "Orbe d'Augmentation", "Orb of Augmentation"];
        yield return ["fra", "FR exact Glassblower", "Bulle de Souffleur de Verre", "Glassblower's Bauble"];
        yield return ["fra", "FR exact Gemcutter", "Prisme de Lapidaire", "Gemcutter's Prism"];
        yield return ["fra", "FR exact Skill Gem", "Gemme de Compétence Brute", "Uncut Skill Gem"];
        yield return ["fra", "FR exact Spirit Gem", "Gemme d'Esprit Brute", "Uncut Spirit Gem"];
        // French - Thane runes
        yield return ["fra", "FR exact Thane Girt", "Rune de la Sauvagerie de Thane Girt", "Thane Girt's Rune of Wildness"];
        yield return ["fra", "FR exact Thane Grannell", "Rune de la Maîtrise de Thane Grannell", "Thane Grannell's Rune of Mastery"];
        yield return ["fra", "FR exact Thane Leld", "Rune du Printemps de Thane Leld", "Thane Leld's Rune of Spring"];
        yield return ["fra", "FR exact Thane Myrk", "Rune de l'Été de Thane Myrk", "Thane Myrk's Rune of Summer"];

        // German - exact matches
        yield return ["deu", "DE exact Chaos", "Chaosorb", "Chaos Orb"];
        yield return ["deu", "DE exact Divine", "Göttlicher Orb", "Divine Orb"];
        yield return ["deu", "DE exact Exalted", "Erhabener Orb", "Exalted Orb"];
        yield return ["deu", "DE exact Skill Gem", "Roher Fertigkeitsedelstein", "Uncut Skill Gem"];

        // Spanish - exact matches
        yield return ["spa", "ES exact Chaos", "Orbe del Caos", "Chaos Orb"];
        yield return ["spa", "ES exact Divine", "Orbe Divino", "Divine Orb"];
        yield return ["spa", "ES exact Exalted", "Orbe Exaltado", "Exalted Orb"];

        // Portuguese - exact matches
        yield return ["por", "PT exact Chaos", "Orbe do Caos", "Chaos Orb"];
        yield return ["por", "PT exact Divine", "Orbe Divino", "Divine Orb"];
        yield return ["por", "PT exact Exalted", "Orbe Exaltado", "Exalted Orb"];

        // Russian - exact matches
        yield return ["rus", "RU exact Chaos", "Хаос", "Chaos Orb"];
        yield return ["rus", "RU exact Divine", "Божественный сфера", "Divine Orb"];
        yield return ["rus", "RU exact Exalted", "Возвышенный сфера", "Exalted Orb"];

        // Japanese - exact matches
        yield return ["jpn", "JP exact Chaos", "カオスオーブ", "Chaos Orb"];
        yield return ["jpn", "JP exact Divine", "神のオーブ", "Divine Orb"];
        yield return ["jpn", "JP exact Exalted", "高貴なオーブ", "Exalted Orb"];
        yield return ["jpn", "JP exact Mirror", "カランドラの鏡", "Mirror of Kalandra"];
        yield return ["jpn", "JP exact Skill Gem", "スキルジェムの原石", "Uncut Skill Gem"];

        // Korean - exact matches
        yield return ["kor", "KO exact Chaos", "카오스 오브", "Chaos Orb"];
        yield return ["kor", "KO exact Divine", "신성한 오브", "Divine Orb"];
        yield return ["kor", "KO exact Exalted", "엑잘티드 오브", "Exalted Orb"];
        yield return ["kor", "KO exact Mirror", "칼란드라의 거울", "Mirror of Kalandra"];
        yield return ["kor", "KO exact Skill Gem", "미가공 스킬 젬", "Uncut Skill Gem"];

        // Chinese Traditional - exact matches
        yield return ["chi_tra", "ZH exact Chaos", "混沌石", "Chaos Orb"];
        yield return ["chi_tra", "ZH exact Divine", "神聖石", "Divine Orb"];
        yield return ["chi_tra", "ZH exact Exalted", "崇高石", "Exalted Orb"];
        yield return ["chi_tra", "ZH exact Alchemy", "點金石", "Orb of Alchemy"];
        yield return ["chi_tra", "ZH exact Skill Gem", "未切割的技能寶石", "Uncut Skill Gem"];
    }
    [Theory]
    [MemberData(nameof(ExactMatchData))]
    public void ExactMatch(string lang, string displayName, string input, string expected)
    {
        var locJson = GetNdjson(lang);
        var cache = CreateCache(lang, locJson);
        var result = cache.ToEnglish(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
        _output.WriteLine($"[PASS] {displayName}: '{input}' → '{result}'");
    }

    public static IEnumerable<object[]> FuzzyMatchData()
    {
        // French - diacritics dropped (é → e)
        yield return ["fra", "FR diacritic Exalte", "Orbe Exalte", "Exalted Orb"];
        yield return ["fra", "FR diacritic Maitrise", "Rune de la Maitrise de Thane Grannell", "Thane Grannell's Rune of Mastery"];
        yield return ["fra", "FR diacritic Ete", "Rune de l'Ete de Thane Myrk", "Thane Myrk's Rune of Summer"];

        // French - apostrophe dropped (l'été → l t / l ete)
        yield return ["fra", "FR apos Eté", "Rune de l'Eté de Thane Myrk", "Thane Myrk's Rune of Summer"];
        yield return ["fra", "FR apos l'été", "Rune de l ete de Thane Myrk", "Thane Myrk's Rune of Summer"];
        yield return ["fra", "FR apos l t", "Rune de l t de Thane Myrk", "Thane Myrk's Rune of Summer"];

        // French - tier suffix stripping (majeur)
        yield return ["fra", "FR tier Chaos majeur", "Orbe du Chaos majeur", "Chaos Orb"];

        // French - single char off (ta → la)
        yield return ["fra", "FR 1-char ta→la", "Rune de ta Sauvagerie de Thane Girt", "Thane Girt's Rune of Wildness"];

        // French - multi char off (Sauvawie → Sauvagerie)
        yield return ["fra", "FR multi Sauvawie", "Rune de la Sauvawie de Thane Girt", "Thane Girt's Rune of Wildness"];

        // French - both diacritics + apos combined (l t = l'été missing both)
        yield return ["fra", "FR combined l t", "Rune de l t de Thane Myrk", "Thane Myrk's Rune of Summer"];

        // German - diacritics (ö → o)
        yield return ["deu", "DE diacritic Göttlicher", "Gottlicher Orb", "Divine Orb"];
        yield return ["deu", "DE diacritic Vergrößerung", "Orb der Vergrosserung", "Orb of Augmentation"];

        // German - tier suffix stripping
        yield return ["deu", "DE tier Chaos", "Chaosorb Stufe 19", "Chaos Orb"];

        // Spanish - diacritics (í → i)
        yield return ["spa", "ES diacritic Lapidario", "Prisma de Lapidario", "Gemcutter's Prism"];

        // Portuguese - diacritics (á → a, ó → o)
        yield return ["por", "PT diacritic Lapidário", "Prisma de Lapidario", "Gemcutter's Prism"];

        // Russian should be exact only (no diacritics fallback needed)

        // Japanese - OCR mangling (missing character)
        yield return ["jpn", "JP OCR miss Chaos", "カオスオフ", "Chaos Orb"];

        // Korean - OCR mangling (missing character)
        yield return ["kor", "KO OCR miss Chaos", "카오스 오", "Chaos Orb"];

        // Chinese - OCR mangling (missing character)
        yield return ["chi_tra", "ZH OCR miss Chaos", "混沌", "Chaos Orb"];
    }

    [Theory]
    [MemberData(nameof(FuzzyMatchData))]
    public void FuzzyMatch(string lang, string displayName, string input, string expected)
    {
        var locJson = GetNdjson(lang);
        var cache = CreateCache(lang, locJson);
        var result = cache.ToEnglish(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
        _output.WriteLine($"[PASS] {displayName}: '{input}' → '{result}'");
    }

    public static IEnumerable<object[]> NoMatchData()
    {
        yield return ["fra", "FR nonsense", "Zarbi Truc Bidule"];
        yield return ["deu", "DE nonsense", "Unsinn Zeug Dings"];
        yield return ["spa", "ES nonsense", "Cosa Absurda"];
        yield return ["por", "PT nonsense", "Coisa Absurda"];
        yield return ["rus", "RU nonsense", "Чушь Какая-то"];
        yield return ["jpn", "JP nonsense", "でたらめな言葉"];
        yield return ["kor", "KO nonsense", "터무니없는 말"];
        yield return ["chi_tra", "ZH nonsense", "胡說八道"];
    }

    [Theory]
    [MemberData(nameof(NoMatchData))]
    public void NoMatch_ReturnsNull(string lang, string displayName, string input)
    {
        var locJson = GetNdjson(lang);
        var cache = CreateCache(lang, locJson);
        var result = cache.ToEnglish(input);
        Assert.Null(result);
        _output.WriteLine($"[PASS] {displayName}: '{input}' → null (as expected)");
    }

    private static string GetNdjson(string lang)
    {
        return lang switch
        {
            "fra" => FrNdjson,
            "deu" => DeNdjson,
            "spa" => EsNdjson,
            "por" => PtNdjson,
            "rus" => RuNdjson,
            "jpn" => JaNdjson,
            "kor" => KoNdjson,
            "chi_tra" => ZhNdjson,
            _ => throw new ArgumentException($"Unknown language: {lang}")
        };
    }
}


