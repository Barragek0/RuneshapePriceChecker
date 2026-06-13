using RuneshapePriceChecker.Startup;
using Xunit;

namespace RuneshapePriceChecker.Tests.Startup;

public class TesseractBootstrapperTests
{
    [Fact]
    public void IsLanguageDataAvailable_English_ReturnsTrue()
    {
        Assert.True(TesseractBootstrapper.IsLanguageDataAvailable("eng"));
    }

    [Fact]
    public void IsLanguageDataAvailable_UnknownLanguage_ReturnsFalse()
    {
        Assert.False(TesseractBootstrapper.IsLanguageDataAvailable("zzz_unknown_lang"));
    }
}
