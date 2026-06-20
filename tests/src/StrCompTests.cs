using Xunit;

namespace RuneshapePriceChecker.Tests;

public sealed class StrCompTests
{
    [Theory]
    [InlineData("Rare Unique Item", "Rare Unique Item", false)]         // same
    [InlineData("Rare Unique Item", "Rare Uniquo Item", true)]          // 1 sub
    [InlineData("Rare Unique Item", "Rare Uniquo Itom", false)]         // 2 subs
    [InlineData("abc", "ab", true)]                                     // 1 deletion
    [InlineData("ab", "abc", true)]                                     // 1 insertion
    [InlineData("abc", "abcd", true)]                                   // 1 insertion
    [InlineData("abc", "abcde", false)]                                 // 2 insertions
    [InlineData("abcde", "abc", false)]                                 // 2 deletions
    public void IsOneCharAway_English(string source, string target, bool expected)
    {
        Assert.Equal(expected, StrComp.IsOneCharAway(source, target));
    }

    [Theory]
    [InlineData("Редкий уникальный предмет", "Редкий уникалъный предмет", true)]  // ь->ъ
    [InlineData("Редкий уникальный предмет", "Релкий уникальный предмет", true)]  // д->л
    [InlineData("Редкий уникальный предмет", "Редкий униквльный предмет", true)]  // а->в
    [InlineData("Редкий уникальный предмет", "Релкий уникалъный предмет", false)] // 2 subs
    [InlineData("ПРИВЕТ", "привет", false)]                                      // case only
    [InlineData("ПРИВЕТ", "приветA", true)]                                      // 1 insertion
    [InlineData("AПРИВЕТ", "привет", true)]                                      // 1 deletion
    public void IsOneCharAway_Cyrillic(string source, string target, bool expected)
    {
        Assert.Equal(expected, StrComp.IsOneCharAway(source, target));
    }

    [Theory]
    [InlineData("Objet rare unique", "Objet nare unique", true)]         // r->n
    [InlineData("Objet rare unique", "Objet rare uniqué", true)]         // e->é
    [InlineData("Objet rare unique", "Objet nare unipue", false)]        // 2 subs
    [InlineData("Seltener einzigartiger", "Seltener einzigaxtiger", true)] // r->x
    [InlineData("Item raro único", "Item raro única", true)]             // o->a
    [InlineData("déjà vu", "deja vu", false)]                            // 2 diacritics off
    public void IsOneCharAway_AccentedLatin(string source, string target, bool expected)
    {
        Assert.Equal(expected, StrComp.IsOneCharAway(source, target));
    }

    [Theory]
    [InlineData("混沌石", "混沌右", true)]               // 石->右 sub
    [InlineData("混沌石", "混囤石", true)]               // 沌->囤 sub
    [InlineData("混沌石", "混囤右", false)]              // 2 subs
    [InlineData("카오스 오브", "카오스 오", true)]       // 1 deletion of 브
    [InlineData("카오스 오", "카오스 오브", true)]       // 1 insertion of 브
    public void IsOneCharAway_Cjk(string source, string target, bool expected)
    {
        Assert.Equal(expected, StrComp.IsOneCharAway(source, target));
    }

    // IsTwoCharsAway returns false when either string is <= 6 chars
    [Theory]
    [InlineData("Rare Unique Item", "Rare Unique Item", false)]         // same
    [InlineData("Rare Unique Item", "Rare Uniquo Item", true)]          // 1 sub
    [InlineData("Rare Unique Item", "Rare Uniquo Itom", true)]          // 2 subs
    [InlineData("Rare Unique Item", "Rare Uniquo Irom", false)]         // 3 subs
    public void IsTwoCharsAway_English(string source, string target, bool expected)
    {
        Assert.Equal(expected, StrComp.IsTwoCharsAway(source, target));
    }

    [Theory]
    [InlineData("Редкий уникальный предмет", "Редкий уникалъный предмет", true)]    // 1 sub
    [InlineData("Редкий уникальный предмет", "Релкий уникалъный предмет", true)]    // 2 subs
    [InlineData("Редкий уникальный предмет", "Релкий уникалъный прелмет", false)]   // 3 subs
    [InlineData("Привет мир друзья", "Привет миp друзья", true)]                    // 1 sub (Latin p)
    public void IsTwoCharsAway_Cyrillic(string source, string target, bool expected)
    {
        Assert.Equal(expected, StrComp.IsTwoCharsAway(source, target));
    }

    [Theory]
    [InlineData("Objet rare unique", "Objet nare unipue", true)]        // 2 subs
    [InlineData("Objet rare unique", "Objot nare unipue", false)]       // 3 subs
    [InlineData("Seltener einzigartiger", "Seltener einzigartigex", true)] // 1 sub
    public void IsTwoCharsAway_AccentedLatin(string source, string target, bool expected)
    {
        Assert.Equal(expected, StrComp.IsTwoCharsAway(source, target));
    }

    [Theory]
    [InlineData("未切割的技能寶石好", "未切割的技惣寶石好", true)] // 能->惣, 1 sub
    [InlineData("未切割的技能寶石好", "未切割的技惣寶右好", true)]  // 能->惣, 石->右, 2 subs
    public void IsTwoCharsAway_Cjk(string source, string target, bool expected)
    {
        Assert.Equal(expected, StrComp.IsTwoCharsAway(source, target));
    }

    [Theory]
    [InlineData("abcdef", "abcdeX")]   // both <= 6
    [InlineData("123456", "12345X")]   // both <= 6
    public void IsTwoCharsAway_ShortStrings_ReturnsFalse(string source, string target)
    {
        Assert.False(StrComp.IsTwoCharsAway(source, target));
    }

    [Theory]
    [InlineData("", "", false)]
    [InlineData("a", "", true)]
    [InlineData("", "a", true)]
    [InlineData("ab", "", false)]
    [InlineData("", "ab", false)]
    public void IsOneCharAway_EdgeCases(string source, string target, bool expected)
    {
        Assert.Equal(expected, StrComp.IsOneCharAway(source, target));
    }

    [Fact]
    public void IsOneCharAway_CaseInsensitiveAcrossScripts()
    {
        Assert.False(StrComp.IsOneCharAway("ПРИВЕТ", "привет"));  // case same
        Assert.False(StrComp.IsOneCharAway("HELLO", "hello"));    // case same
        Assert.True(StrComp.IsOneCharAway("ПРИВЕТ", "ПРИВЕК"));   // Т->К, 1 sub
        Assert.True(StrComp.IsOneCharAway("HELLO", "HELXLO"));    // L->X, 1 sub
    }
}
