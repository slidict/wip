using System.Text.Json;

namespace Wip.Execution;

internal static class ResourceJson
{
    internal static string? Text(JsonElement record, string key) =>
        record.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static IReadOnlyList<JsonElement> Records(string output, bool allowEmpty = false)
    {
        if (allowEmpty && string.IsNullOrWhiteSpace(output)) return [];
        if (string.IsNullOrWhiteSpace(output)) throw new WipException("Resource backend returned empty JSON; state is unknown");
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return [document.RootElement.Clone()];
            if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.EnumerateArray().All(r => r.ValueKind == JsonValueKind.Object))
                return document.RootElement.EnumerateArray().Select(r => r.Clone()).ToArray();
        }
        catch (JsonException)
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 1)
            {
                var records = new List<JsonElement>();
                try
                {
                    foreach (var line in lines)
                    {
                        using var document = JsonDocument.Parse(line);
                        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                        records.Add(document.RootElement.Clone());
                    }
                    return records;
                }
                catch (JsonException) { }
            }
        }
        throw new WipException("Resource backend returned malformed JSON; state is unknown");
    }
}
