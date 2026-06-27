using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class UniqueCategoryLookupTests
{

    [Fact]
    public void SupportScouringFlame_DoesNotMatchUniqueRing()
    {
        // The word "SCOURING" contains "RING" as a substring (SCOU-RING).
        // Word-boundary matching must reject this.
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("SUPPORT SCOURING FLAME").ToArray();
        Assert.DoesNotContain(candidates, c => c.StartsWith("UNIQUE RING", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("RING")]                         // English exact
    [InlineData("MAGIC RING")]                   // English multi-word + keyword
    [InlineData("UNIQUE RING")]                  // Already unique-prefixed
    [InlineData("GOLD RING")]                    // English multi-word + keyword
    [InlineData("ANNEAU")]                       // French
    [InlineData("ANNEAU DU CHAOS")]              // French phrase
    [InlineData("ANILLO")]                       // Spanish
    [InlineData("ANEL")]                         // Portuguese
    [InlineData("КОЛЬЦО")]                       // Russian
    [InlineData("戒指")]                          // Chinese
    [InlineData("リング")]                         // Japanese
    [InlineData("반지")]                          // Korean
    public void LegitimateRingKeyword_MatchesUniqueRing(string itemName)
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates(itemName).ToArray();
        Assert.Contains(candidates, c => c.StartsWith("UNIQUE RING", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("SCOURING")]                     // Contains "RING" inside word
    [InlineData("SCOURING SALT")]                // "RING" inside "SCOURING"
    [InlineData("RAINBOW")]                      // Contains "BOW" inside word (BOW is not a map keyword)
    [InlineData("ELBOW")]                        // Contains "BOW" inside word
    [InlineData("CANELO")]                       // Contains "ANEL" (pt: ring) inside word
    [InlineData("FOCUSED")]                      // Contains "FOCUS" inside word (FOCUS not a keyword)
    [InlineData("BOWLING")]                      // Contains "BOW" inside word
    [InlineData("RINGLET")]                      // "RING" at word start but not a whole word
    [InlineData("SPIRIT")]                       // Does not contain any base type keyword
    public void SubstringKeyword_DoesNotMatchCategory(string itemName)
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates(itemName).ToArray();
        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("RING")]                         // English keyword (in map)
    [InlineData("HELM")]                         // English keyword (in map)
    [InlineData("TALISMAN")]                     // English keyword (in map)
    public void LegitimateKeyword_MatchesCorrectCategory(string itemName)
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates(itemName).ToArray();
        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.StartsWith("UNIQUE ", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RingKeyword_DoesNotMatchBelt()
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("RING").ToArray();
        Assert.All(candidates, c => Assert.Contains("RING", c, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, c => c.Contains("BELT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, c => c.Contains("AMULET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BowKeyword_DoesNotMatchElbow()
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("ELBOW").ToArray();
        Assert.Empty(candidates);
    }

    [Fact]
    public void FocusKeyword_DoesNotMatchFocused()
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("FOCUSED").ToArray();
        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("ЛУК")]                          // Russian for BOW, exact word boundary
    [InlineData("ЛУК ДРОКОНА")]                   // Dragon Bow, keyword at word boundary
    [InlineData("ЩИТ")]                          // Russian for SHIELD
    [InlineData("ЩИТ ВААЛА")]                    // Vaal Shield
    [InlineData("ШЛЕМ")]                         // Russian for HELMET
    [InlineData("ПЕРЧАТКИ")]                     // Russian for GLOVES
    [InlineData("САПОГИ")]                       // Russian for BOOTS
    public void ForeignKeyword_MatchesCorrectCategory(string itemName)
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates(itemName).ToArray();
        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.StartsWith("UNIQUE ", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForeignKeyword_InsideLongerWord_DoesNotMatch()
    {
        // "ЛУК" (bow) appears inside "ПОЛУКРУГ" (semicircle) as a substring.
        // Word-boundary matching must reject this.
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("ПОЛУКРУГ").ToArray();
        Assert.Empty(candidates);
    }

    [Fact]
    public void MultipleRingLikeWords_OnlyWholeWordMatches()
    {
        // "RINGING" contains "RING" at the start but is a different word entirely
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("RINGING").ToArray();
        Assert.Empty(candidates);
    }

    [Fact]
    public void AnelInsideLongerWord_DoesNotMatch()
    {
        // "ANEL" (Portuguese for ring) inside "CANELO"
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("CANELO").ToArray();
        Assert.Empty(candidates);
    }

    [Fact]
    public void ArmeInsideLongerWord_DoesNotMatch()
    {
        // "ARME" (French for weapon) inside "ARMEMENT"
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("ARMEMENT").ToArray();
        Assert.Empty(candidates);
    }

    [Fact]
    public void ArmaInsideLongerWord_DoesNotMatch()
    {
        // "ARMA" (Italian for weapon) inside "ARMATA"
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("ARMATA").ToArray();
        Assert.Empty(candidates);
    }

    [Fact]
    public void EnglishNonCategoryItem_ReturnsNoCandidates()
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("CHAOS ORB").ToArray();
        Assert.Empty(candidates);
    }

    [Fact]
    public void EmptyString_ReturnsNoCandidates()
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("").ToArray();
        Assert.Empty(candidates);
    }

    [Fact]
    public void WhitespaceString_ReturnsNoCandidates()
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates("   ").ToArray();
        Assert.Empty(candidates);
    }

    [Fact]
    public void Null_ReturnsNoCandidates()
    {
        var candidates = ItemNameParser.BuildUniqueCategoryLookupCandidates(null!).ToArray();
        Assert.Empty(candidates);
    }
}
