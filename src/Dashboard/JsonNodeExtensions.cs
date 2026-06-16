using System.Text.Json.Nodes;

namespace RuneshapePriceChecker.App.Dashboard;

internal static class JsonNodeExtensions
{
    public static string Str(this JsonNode? node, string key, string fallback = "")
    {
        return node?[key]?.GetValue<string>() ?? fallback;
    }

    public static T Val<T>(this JsonNode? node, string key, T fallback) where T : struct
    {
        return node?[key]?.GetValue<T>() ?? fallback;
    }

    public static T? ValOrNull<T>(this JsonNode? node, string key) where T : struct
    {
        return node?[key]?.GetValue<T>();
    }
}
