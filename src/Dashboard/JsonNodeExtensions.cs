using System.Text.Json.Nodes;

namespace RuneshapePriceChecker.App.Dashboard;

internal static class JsonNodeExtensions
{
    public static string Str(this JsonNode? node, string key, string fallback = "") =>
        node?[key]?.GetValue<string>() ?? fallback;

    public static T Val<T>(this JsonNode? node, string key, T fallback) where T : struct =>
        node?[key]?.GetValue<T>() ?? fallback;

    public static T? ValOrNull<T>(this JsonNode? node, string key) where T : struct =>
        node?[key]?.GetValue<T>();
}
