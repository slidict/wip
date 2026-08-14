using System.Text.Json.Nodes;

namespace Wip.Tests;

/// <summary>
/// Renders the loosely typed trees the port produces as JSON, so they can be compared
/// against expectations Ruby's <c>JSON.generate</c> wrote.
/// </summary>
internal static class RubyJson
{
    internal static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        long number => JsonValue.Create(number),
        int number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        IReadOnlyList<string> strings => new JsonArray(strings.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
        List<object?> list => new JsonArray(list.Select(ToJson).ToArray()),
        OrderedDictionary<string, object?> mapping => ToJsonObject(mapping),
        OrderedDictionary<string, string> mapping => ToJsonObject(mapping),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonObject ToJsonObject(OrderedDictionary<string, object?> mapping)
    {
        var result = new JsonObject();
        foreach (var (key, value) in mapping)
        {
            result[key] = ToJson(value);
        }

        return result;
    }

    private static JsonObject ToJsonObject(OrderedDictionary<string, string> mapping)
    {
        var result = new JsonObject();
        foreach (var (key, value) in mapping)
        {
            result[key] = JsonValue.Create(value);
        }

        return result;
    }

    internal static string Canonical(JsonNode? node) => node?.ToJsonString() ?? "null";
}
