using System.IO;
using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RuneshapePriceChecker.App;
using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

public sealed class MultiLanguageWorkerTests
{
    private static readonly string[] EnglishUncutGemNames = ["Uncut Skill Gem", "Uncut Support Gem", "Uncut Spirit Gem"];
    private static readonly string[] EnglishSupportGemName = ["Support Gem"];

    [Fact]
    public void IsRareUniqueItem_EnglishExact_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Rare Unique Item"])!);
        Assert.True((bool)method!.Invoke(null, ["Very Rare Unique Item"])!);
    }

    [Fact]
    public void IsRareUniqueItem_EnglishCaseInsensitive_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["rare unique item"])!);
        Assert.True((bool)method!.Invoke(null, ["RARE UNIQUE ITEM"])!);
        Assert.True((bool)method!.Invoke(null, ["VERY RARE UNIQUE ITEM"])!);
    }

    [Fact]
    public void IsRareUniqueItem_RussianExact_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Редкий уникальный предмет"])!);
    }

    [Fact]
    public void IsRareUniqueItem_RussianCaseInsensitive_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["редкий уникальный предмет"])!);
        Assert.True((bool)method!.Invoke(null, ["РЕДКИЙ УНИКАЛЬНЫЙ ПРЕДМЕТ"])!);
    }

    [Fact]
    public void IsRareUniqueItem_RussianStartsWith_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Редкий уникальный предмет (редкий)"])!);
        Assert.True((bool)method!.Invoke(null, ["Редкий уникальный"])!);
    }

    [Fact]
    public void IsRareUniqueItem_RussianFuzzyOneChar_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // OCR misread "ь" as "ъ"
        Assert.True((bool)method!.Invoke(null, ["Редкий уникалъный предмет"])!);
        // OCR misread "д" as "л"
        Assert.True((bool)method!.Invoke(null, ["Релкий уникальный предмет"])!);
    }

    [Fact]
    public void IsRareUniqueItem_RussianFuzzyTwoChars_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // OCR misread "ед" as "ел"
        Assert.True((bool)method!.Invoke(null, ["Релкий уникалъный предмет"])!);
    }

    [Fact]
    public void IsRareUniqueItem_RussianOtherPhrase_ReturnsFalse()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method!.Invoke(null, ["Хаос"])!);
        Assert.False((bool)method!.Invoke(null, ["Уникальное кольцо"])!);
    }

    [Fact]
    public void IsRareUniqueItem_GermanExact_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Seltener einzigartiger Gegenstand"])!);
    }

    [Fact]
    public void IsRareUniqueItem_GermanStartsWith_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Seltener einzigartiger"])!);
        Assert.True((bool)method!.Invoke(null, ["Seltener einzigartiger Gegenstand (selten)"])!);
    }

    [Fact]
    public void IsRareUniqueItem_GermanFuzzyOneChar_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // OCR misread "r" as "n"
        Assert.True((bool)method!.Invoke(null, ["Seltener einzigantiger Gegenstand"])!);
    }

    [Fact]
    public void IsRareUniqueItem_GermanOtherPhrase_ReturnsFalse()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method!.Invoke(null, ["Chaosorb"])!);
        Assert.False((bool)method!.Invoke(null, ["Einzigartiger Ring"])!);
    }

    [Fact]
    public void IsRareUniqueItem_FrenchExact_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Objet rare unique"])!);
    }

    [Fact]
    public void IsRareUniqueItem_FrenchCaseInsensitive_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["objet rare unique"])!);
        Assert.True((bool)method!.Invoke(null, ["OBJET RARE UNIQUE"])!);
    }

    [Fact]
    public void IsRareUniqueItem_FrenchStartsWith_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Objet rare unique (rare)"])!);
    }

    [Fact]
    public void IsRareUniqueItem_FrenchFuzzyOneChar_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // OCR misread "r" as "n"
        Assert.True((bool)method!.Invoke(null, ["Objet nare unique"])!);
    }

    [Fact]
    public void IsRareUniqueItem_FrenchOtherPhrase_ReturnsFalse()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method!.Invoke(null, ["Orbe du Chaos"])!);
        Assert.False((bool)method!.Invoke(null, ["Anneau unique"])!);
    }

    [Fact]
    public void IsRareUniqueItem_SpanishExact_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Objeto raro único"])!);
    }

    [Fact]
    public void IsRareUniqueItem_SpanishCaseInsensitive_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["objeto raro único"])!);
        Assert.True((bool)method!.Invoke(null, ["OBJETO RARO ÚNICO"])!);
    }

    [Fact]
    public void IsRareUniqueItem_SpanishStartsWith_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Objeto raro único (raro)"])!);
    }

    [Fact]
    public void IsRareUniqueItem_SpanishFuzzyOneChar_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // OCR misread "r" as "n"
        Assert.True((bool)method!.Invoke(null, ["Objeto naro único"])!);
    }

    [Fact]
    public void IsRareUniqueItem_SpanishOtherPhrase_ReturnsFalse()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method!.Invoke(null, ["Orbe del Caos"])!);
        Assert.False((bool)method!.Invoke(null, ["Anillo único"])!);
    }

    [Fact]
    public void IsRareUniqueItem_PortugueseExact_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Item raro único"])!);
    }

    [Fact]
    public void IsRareUniqueItem_PortugueseCaseInsensitive_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["item raro único"])!);
        Assert.True((bool)method!.Invoke(null, ["ITEM RARO ÚNICO"])!);
    }

    [Fact]
    public void IsRareUniqueItem_PortugueseStartsWith_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Item raro único (raro)"])!);
    }

    [Fact]
    public void IsRareUniqueItem_PortugueseFuzzyOneChar_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // OCR misread "r" as "n"
        Assert.True((bool)method!.Invoke(null, ["Item naro único"])!);
    }

    [Fact]
    public void IsRareUniqueItem_PortugueseOtherPhrase_ReturnsFalse()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsRareUniqueItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method!.Invoke(null, ["Orbe do Caos"])!);
        Assert.False((bool)method!.Invoke(null, ["Anel único"])!);
    }

    [Fact]
    public void IsPricedUncut_EnglishUncutSkillGem_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsPricedUncut",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Uncut Skill Gem"])!);
    }

    [Fact]
    public void IsPricedUncut_EnglishUncutSupportGem_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsPricedUncut",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Uncut Support Gem"])!);
    }

    [Fact]
    public void IsPricedUncut_EnglishUncutSpiritGem_ReturnsTrue()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsPricedUncut",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, ["Uncut Spirit Gem"])!);
    }

    [Fact]
    public void IsPricedUncut_NonEnglishUncutGem_ReturnsFalse()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsPricedUncut",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // IsPricedUncut only matches English prefixes; non-English names
        // are handled by the translator before BuildUnpriceableBanner.
        Assert.False((bool)method!.Invoke(null, ["Gemme de Compétence Brute"])!);
        Assert.False((bool)method!.Invoke(null, ["Roher Fertigkeitsedelstein"])!);
        Assert.False((bool)method!.Invoke(null, ["Gema de Habilidad Bruta"])!);
        Assert.False((bool)method!.Invoke(null, ["Gema de Habilidade Bruta"])!);
        Assert.False((bool)method!.Invoke(null, ["Неогранённый самоцвет умений"])!);
        Assert.False((bool)method!.Invoke(null, ["スキルジェムの原石"])!);
        Assert.False((bool)method!.Invoke(null, ["미가공 스킬 젬"])!);
        Assert.False((bool)method!.Invoke(null, ["未切割的技能寶石"])!);
    }

    [Fact]
    public void IsPricedUncut_RegularItem_ReturnsFalse()
    {
        var method = typeof(LeaguePricingWorker).GetMethod("IsPricedUncut",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False((bool)method!.Invoke(null, ["Chaos Orb"])!);
        Assert.False((bool)method!.Invoke(null, ["Divine Orb"])!);
    }

    private static readonly string FrGemNdjson = """
{"name":"Gemme de Compétence Brute","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Gemme de Soutien Brute","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Gemme d'Esprit Brute","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

    private static readonly string EsGemNdjson = """
{"name":"Gema de Habilidad Bruta","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Gema de Apoyo Bruta","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Gema de Espíritu Bruta","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

    private static readonly string DeGemNdjson = """
{"name":"Roher Fertigkeitsedelstein","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Roher Unterstützungsedelstein","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Roher Geistesedelstein","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

    private static readonly string RuGemNdjson = """
{"name":"Неогранённый самоцвет умений","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Неогранённый самоцвет поддержки","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Неогранённый самоцвет духа","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

    private static readonly string PtGemNdjson = """
{"name":"Gema de Habilidade Bruta","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"Gema de Suporte Bruta","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"Gema de Espírito Bruta","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

    private static readonly string JaGemNdjson = """
{"name":"スキルジェムの原石","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"サポートジェムの原石","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"スピリットジェムの原石","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

    private static readonly string KoGemNdjson = """
{"name":"미가공 스킬 젬","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"미가공 보조 젬","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"미가공 정신력 젬","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

    private static readonly string ZhGemNdjson = """
{"name":"未切割的技能寶石","refName":"Uncut Skill Gem","namespace":"ITEM"}
{"name":"未切割的輔助寶石","refName":"Uncut Support Gem","namespace":"ITEM"}
{"name":"精魂寶石","refName":"Uncut Spirit Gem","namespace":"ITEM"}
""";

    private static TranslationCache CreateCache(string lang, string ndjson)
    {
        var ocrDir = Path.Combine(Path.GetTempPath(), "RPC-Test", Guid.NewGuid().ToString());
        var cache = new TranslationCache(new HttpClient(), NullLogger<TranslationCache>.Instance, ocrDir);
        cache.LoadFromString(lang, ndjson);
        return cache;
    }

    private static ItemNameTranslator CreateTranslator(string lang, string ndjson)
    {
        var cache = CreateCache(lang, ndjson);
        var translator = new ItemNameTranslator(NullLogger<ItemNameTranslator>.Instance, cache);
        translator.SetLanguage(lang);
        return translator;
    }

    private static string? InvokeBuildUnpriceableBanner(string[] names, ItemNameTranslator? translator = null)
    {
        var method = typeof(LeaguePricingWorker).GetMethod("BuildUnpriceableBanner",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null) return null;
        return method.Invoke(null, [names, translator]) as string;
    }

    [Fact]
    public void BuildUnpriceableBanner_FrenchUncutGemsWithTranslator_NotFlagged()
    {
        // French uncut gems should translate to English uncut gems (priced)
        var translator = CreateTranslator("fra", FrGemNdjson);
        var result = InvokeBuildUnpriceableBanner(
            ["Gemme de Compétence Brute", "Gemme de Soutien Brute", "Gemme d'Esprit Brute"],
            translator);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_SpanishUncutGemsWithTranslator_NotFlagged()
    {
        var translator = CreateTranslator("spa", EsGemNdjson);
        var result = InvokeBuildUnpriceableBanner(
            ["Gema de Habilidad Bruta", "Gema de Apoyo Bruta", "Gema de Espíritu Bruta"],
            translator);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_GermanUncutGemsWithTranslator_NotFlagged()
    {
        var translator = CreateTranslator("deu", DeGemNdjson);
        var result = InvokeBuildUnpriceableBanner(
            ["Roher Fertigkeitsedelstein", "Roher Unterstützungsedelstein", "Roher Geistesedelstein"],
            translator);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_RussianUncutGemsWithTranslator_NotFlagged()
    {
        var translator = CreateTranslator("rus", RuGemNdjson);
        var result = InvokeBuildUnpriceableBanner(
            ["Неогранённый самоцвет умений", "Неогранённый самоцвет поддержки", "Неогранённый самоцвет духа"],
            translator);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_PortugueseUncutGemsWithTranslator_NotFlagged()
    {
        var translator = CreateTranslator("por", PtGemNdjson);
        var result = InvokeBuildUnpriceableBanner(
            ["Gema de Habilidade Bruta", "Gema de Suporte Bruta", "Gema de Espírito Bruta"],
            translator);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_KoreanUncutGemsWithTranslator_NotFlagged()
    {
        var translator = CreateTranslator("kor", KoGemNdjson);
        var result = InvokeBuildUnpriceableBanner(
            ["미가공 스킬 젬", "미가공 보조 젬", "미가공 정신력 젬"],
            translator);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_ChineseUncutGemsWithTranslator_NotFlagged()
    {
        var translator = CreateTranslator("chi_tra", ZhGemNdjson);
        var result = InvokeBuildUnpriceableBanner(
            ["未切割的技能寶石", "未切割的輔助寶石", "精魂寶石"],
            translator);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_FrenchBareSkillGemWithoutTranslator_NotFlagged()
    {
        // Without a translator, French names don't match English prefixes
        var result = InvokeBuildUnpriceableBanner(["Gemme de Compétence"]);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_EnglishUncutGemsWithoutTranslator_NotFlagged()
    {
        var result = InvokeBuildUnpriceableBanner(EnglishUncutGemNames);
        Assert.Null(result);
    }

    [Fact]
    public void BuildUnpriceableBanner_EnglishSupportGemWithoutTranslator_Flagged()
    {
        var result = InvokeBuildUnpriceableBanner(EnglishSupportGemName);
        Assert.NotNull(result);
    }
}
