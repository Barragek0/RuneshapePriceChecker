using RuneshapePriceChecker.Pricing;
using Xunit;

namespace RuneshapePriceChecker.Tests.Pricing;

public class UniqueItemTypeLookupPrefixTests
{
    [Fact]
    public void TryGetCategory_KnownItem_ReturnsCategory()
    {
        var category = UniqueItemTypeLookup.TryGetCategory("MJOLNER");
        Assert.NotNull(category);
    }

    [Fact]
    public void TryGetCategory_UnknownItem_ReturnsNull()
    {
        var category = UniqueItemTypeLookup.TryGetCategory("ZZZ_UNKNOWN_ITEM_XYZ");
        Assert.Null(category);
    }

    [Fact]
    public void TryGetCategory_CaseInsensitive_Works()
    {
        var category = UniqueItemTypeLookup.TryGetCategory("mjolner");
        Assert.NotNull(category);
    }

    [Fact]
    public void TryGetCategory_Null_ReturnsNull()
    {
        Assert.Null(UniqueItemTypeLookup.TryGetCategory(null!));
    }

    [Fact]
    public void TryGetCategory_Empty_ReturnsNull()
    {
        Assert.Null(UniqueItemTypeLookup.TryGetCategory(""));
    }

    [Fact]
    public void TryGetCategory_Whitespace_ReturnsNull()
    {
        Assert.Null(UniqueItemTypeLookup.TryGetCategory("   "));
    }

    // ── Category coverage: one known item per major category ──

    [Fact]
    public void TryGetCategory_OneHandMace_ReturnsCategory()
    {
        Assert.Equal("ONE HAND MACE", UniqueItemTypeLookup.TryGetCategory("MJOLNER"));
    }

    [Fact]
    public void TryGetCategory_TwoHandMace_ReturnsCategory()
    {
        Assert.Equal("TWO HAND MACE", UniqueItemTypeLookup.TryGetCategory("HOGHUNT"));
    }

    [Fact]
    public void TryGetCategory_Bow_ReturnsCategory()
    {
        Assert.Equal("BOW", UniqueItemTypeLookup.TryGetCategory("WIDOWHAIL"));
    }

    [Fact]
    public void TryGetCategory_Crossbow_ReturnsCategory()
    {
        Assert.Equal("CROSSBOW", UniqueItemTypeLookup.TryGetCategory("MIST WHISPER"));
    }

    [Fact]
    public void TryGetCategory_Quarterstaff_ReturnsCategory()
    {
        Assert.Equal("QUARTERSTAFF", UniqueItemTypeLookup.TryGetCategory("THE BLOOD THORN"));
    }

    [Fact]
    public void TryGetCategory_Spear_ReturnsCategory()
    {
        Assert.Equal("SPEAR", UniqueItemTypeLookup.TryGetCategory("SPLINTER OF LORRATA"));
    }

    [Fact]
    public void TryGetCategory_Staff_ReturnsCategory()
    {
        Assert.Equal("STAFF", UniqueItemTypeLookup.TryGetCategory("DUSK VIGIL"));
    }

    [Fact]
    public void TryGetCategory_Wand_ReturnsCategory()
    {
        Assert.Equal("WAND", UniqueItemTypeLookup.TryGetCategory("LIFESPRIG"));
    }

    [Fact]
    public void TryGetCategory_Sceptre_ReturnsCategory()
    {
        Assert.Equal("SCEPTRE", UniqueItemTypeLookup.TryGetCategory("THE DARK DEFILER"));
    }

    [Fact]
    public void TryGetCategory_Focus_ReturnsCategory()
    {
        Assert.Equal("FOCUS", UniqueItemTypeLookup.TryGetCategory("DEATHRATTLE"));
    }

    [Fact]
    public void TryGetCategory_Shield_ReturnsCategory()
    {
        Assert.Equal("SHIELD", UniqueItemTypeLookup.TryGetCategory("DIONADAIR"));
    }

    [Fact]
    public void TryGetCategory_Quiver_ReturnsCategory()
    {
        Assert.Equal("QUIVER", UniqueItemTypeLookup.TryGetCategory("BLACKGLEAM"));
    }

    [Fact]
    public void TryGetCategory_Talisman_ReturnsCategory()
    {
        Assert.Equal("TALISMAN", UniqueItemTypeLookup.TryGetCategory("AMOR MANDRAGORA"));
    }

    [Fact]
    public void TryGetCategory_BodyArmour_ReturnsCategory()
    {
        Assert.Equal("BODY ARMOUR", UniqueItemTypeLookup.TryGetCategory("BRAMBLEJACK"));
    }

    [Fact]
    public void TryGetCategory_Belt_ReturnsCategory()
    {
        Assert.Equal("BELT", UniqueItemTypeLookup.TryGetCategory("ZERPHI'S GENESIS"));
    }

    // ── Prefix match behavior ──

    [Fact]
    public void TryGetCategory_PrefixMatchWithSuffix_ReturnsCategory()
    {
        // Prefix matching: "MJOLNER" should match "MJOLNER Ancient Hammer" etc.
        var category = UniqueItemTypeLookup.TryGetCategory("MJOLNER Gavel");
        Assert.Equal("ONE HAND MACE", category);
    }

    [Fact]
    public void TryGetCategory_Lowercase_StillMatches()
    {
        Assert.Equal("BOW", UniqueItemTypeLookup.TryGetCategory("widowhail"));
    }
}
