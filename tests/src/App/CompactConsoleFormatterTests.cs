using System.Reflection;
using Microsoft.Extensions.Logging;
using RuneshapePriceChecker.App;
using Xunit;

namespace RuneshapePriceChecker.Tests.App;

public class CompactConsoleFormatterTests
{
    private static readonly MethodInfo ShortenCategoryMethod = typeof(CompactConsoleFormatter)
        .GetMethod("ShortenCategory", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetLogLevelTextMethod = typeof(CompactConsoleFormatter)
        .GetMethod("GetLogLevelText", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetLogLevelColorMethod = typeof(CompactConsoleFormatter)
        .GetMethod("GetLogLevelColor", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string ShortenCategory(string category)
    {
        return (string)ShortenCategoryMethod.Invoke(null, [category])!;
    }

    private static string GetLogLevelText(LogLevel level)
    {
        return (string)GetLogLevelTextMethod.Invoke(null, [level])!;
    }

    private static ConsoleColor GetLogLevelColor(LogLevel level)
    {
        return (ConsoleColor)GetLogLevelColorMethod.Invoke(null, [level])!;
    }

    // ── ShortenCategory ──

    [Fact]
    public void ShortenCategory_RuneshapePrefix_StripsPrefix()
    {
        var result = ShortenCategory("RuneshapePriceChecker.OCR.OcrEngine");
        Assert.Equal("OCR.OcrEngine", result);
    }

    [Fact]
    public void ShortenCategory_RuneshapePrefixSingleSegment_ReturnsRemainder()
    {
        var result = ShortenCategory("RuneshapePriceChecker.App");
        Assert.Equal("App", result);
    }

    [Fact]
    public void ShortenCategory_HttpClientPrefix_ShortensToHttpClient()
    {
        var result = ShortenCategory("System.Net.Http.HttpClient.SomeClient");
        Assert.Equal("HttpClient.SomeClient", result);
    }

    [Fact]
    public void ShortenCategory_SystemNetHttpPrefix_StripsPrefix()
    {
        var result = ShortenCategory("System.Net.Http.Logging");
        Assert.Equal("Logging", result);
    }

    [Fact]
    public void ShortenCategory_UnrecognizedPrefix_ReturnsOriginal()
    {
        var result = ShortenCategory("Microsoft.Extensions.Logging.Console");
        Assert.Equal("Microsoft.Extensions.Logging.Console", result);
    }

    [Fact]
    public void ShortenCategory_EmptyString_ReturnsEmpty()
    {
        var result = ShortenCategory("");
        Assert.Equal("", result);
    }

    [Fact]
    public void ShortenCategory_ExactRuneshapePrefix_ReturnsEmpty()
    {
        var result = ShortenCategory("RuneshapePriceChecker.");
        Assert.Equal("", result);
    }

    [Fact]
    public void ShortenCategory_ExactHttpClientPrefix_ReturnsHttpClientDot()
    {
        var result = ShortenCategory("System.Net.Http.HttpClient.");
        Assert.Equal("HttpClient.", result);
    }

    // ── GetLogLevelText ──

    [Fact]
    public void GetLogLevelText_Trace_ReturnsTrace()
    {
        Assert.Equal("trace", GetLogLevelText(LogLevel.Trace));
    }

    [Fact]
    public void GetLogLevelText_Debug_ReturnsDebug()
    {
        Assert.Equal("debug", GetLogLevelText(LogLevel.Debug));
    }

    [Fact]
    public void GetLogLevelText_Information_ReturnsInfo()
    {
        Assert.Equal("info", GetLogLevelText(LogLevel.Information));
    }

    [Fact]
    public void GetLogLevelText_Warning_ReturnsWarn()
    {
        Assert.Equal("warn", GetLogLevelText(LogLevel.Warning));
    }

    [Fact]
    public void GetLogLevelText_Error_ReturnsError()
    {
        Assert.Equal("error", GetLogLevelText(LogLevel.Error));
    }

    [Fact]
    public void GetLogLevelText_Critical_ReturnsCritical()
    {
        Assert.Equal("critical", GetLogLevelText(LogLevel.Critical));
    }

    [Fact]
    public void GetLogLevelText_None_ReturnsNone()
    {
        Assert.Equal("none", GetLogLevelText(LogLevel.None));
    }

    // ── GetLogLevelColor ──

    [Fact]
    public void GetLogLevelColor_Critical_ReturnsRed()
    {
        Assert.Equal(ConsoleColor.Red, GetLogLevelColor(LogLevel.Critical));
    }

    [Fact]
    public void GetLogLevelColor_Error_ReturnsRed()
    {
        Assert.Equal(ConsoleColor.Red, GetLogLevelColor(LogLevel.Error));
    }

    [Fact]
    public void GetLogLevelColor_Warning_ReturnsYellow()
    {
        Assert.Equal(ConsoleColor.Yellow, GetLogLevelColor(LogLevel.Warning));
    }

    [Fact]
    public void GetLogLevelColor_Information_ReturnsGray()
    {
        Assert.Equal(ConsoleColor.Gray, GetLogLevelColor(LogLevel.Information));
    }

    [Fact]
    public void GetLogLevelColor_Debug_ReturnsDarkGray()
    {
        Assert.Equal(ConsoleColor.DarkGray, GetLogLevelColor(LogLevel.Debug));
    }

    [Fact]
    public void GetLogLevelColor_Trace_ReturnsDarkGray()
    {
        Assert.Equal(ConsoleColor.DarkGray, GetLogLevelColor(LogLevel.Trace));
    }

    [Fact]
    public void GetLogLevelColor_None_ReturnsGray()
    {
        Assert.Equal(ConsoleColor.Gray, GetLogLevelColor(LogLevel.None));
    }
}
