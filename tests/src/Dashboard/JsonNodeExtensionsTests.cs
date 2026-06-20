using System.Text.Json.Nodes;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class JsonNodeExtensionsTests
{

    [Fact]
    public void Str_NullNode_ReturnsFallback()
    {
        Assert.Equal("fallback", ((JsonNode?)null).Str("key", "fallback"));
    }

    [Fact]
    public void Str_MissingKey_ReturnsFallback()
    {
        var node = JsonNode.Parse("""{"other":"value"}""");
        Assert.Equal("fallback", node.Str("key", "fallback"));
    }

    [Fact]
    public void Str_PresentKey_ReturnsValue()
    {
        var node = JsonNode.Parse("""{"key":"hello"}""");
        Assert.Equal("hello", node.Str("key", "fallback"));
    }

    [Fact]
    public void Str_EmptyStringValue_ReturnsEmpty()
    {
        var node = JsonNode.Parse("""{"key":""}""");
        Assert.Equal("", node.Str("key", "fallback"));
    }

    [Fact]
    public void Str_DefaultFallback_ReturnsEmpty()
    {
        Assert.Equal("", ((JsonNode?)null).Str("key"));
    }

    [Fact]
    public void Str_IntegerValue_ThrowsInvalidOperation()
    {
        // GetValue<string>() on an integer node throws InvalidOperationException
        var node = JsonNode.Parse("""{"key":42}""");
        _ = Assert.Throws<InvalidOperationException>(() => node.Str("key", "fallback"));
    }

    [Fact]
    public void Val_NullNode_ReturnsFallback()
    {
        Assert.Equal(99, ((JsonNode?)null).Val("key", 99));
    }

    [Fact]
    public void Val_MissingKey_ReturnsFallback()
    {
        var node = JsonNode.Parse("""{"other":1}""");
        Assert.Equal(99, node.Val("key", 99));
    }

    [Fact]
    public void Val_PresentKey_ReturnsValue()
    {
        var node = JsonNode.Parse("""{"key":42}""");
        Assert.Equal(42, node.Val("key", 99));
    }

    [Fact]
    public void Val_StringValue_ThrowsInvalidOperation()
    {
        // GetValue<int>() on a string node throws InvalidOperationException
        var node = JsonNode.Parse("""{"key":"hello"}""");
        _ = Assert.Throws<InvalidOperationException>(() => node.Val("key", 99));
    }

    [Fact]
    public void ValOrNull_NullNode_ReturnsNull()
    {
        Assert.Null(((JsonNode?)null).ValOrNull<int>("key"));
    }

    [Fact]
    public void ValOrNull_MissingKey_ReturnsNull()
    {
        var node = JsonNode.Parse("""{"other":1}""");
        Assert.Null(node.ValOrNull<int>("key"));
    }

    [Fact]
    public void ValOrNull_PresentKey_ReturnsValue()
    {
        var node = JsonNode.Parse("""{"key":42}""");
        Assert.Equal(42, node.ValOrNull<int>("key"));
    }

    [Fact]
    public void ValOrNull_ZeroValue_ReturnsZero()
    {
        var node = JsonNode.Parse("""{"key":0}""");
        Assert.Equal(0, node.ValOrNull<int>("key"));
    }
}
