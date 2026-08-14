using System.Text.Json.Nodes;
using Wip.Yaml;

namespace Wip.Tests;

public class YamlScalarTests
{
    /// <summary>
    /// Psych resolutions this port knowingly does not reproduce; see the remarks on
    /// <see cref="YamlScalarResolver"/>. Listing them here keeps the divergence visible
    /// instead of hiding it behind a missing test case.
    /// </summary>
    private static readonly Dictionary<string, string> KnownDivergences = new(StringComparer.Ordinal)
    {
        ["12:30"] = "sexagesimal, read as a string",
        ["2024-01-02"] = "date, read as a string (Psych raised on it under wip's loader settings)",
    };

    public static IEnumerable<object[]> Cases() => GoldenCorpus.UnitCases("yaml_scalars", arrayProperty: null);

    [Theory]
    [MemberData(nameof(Cases))]
    public void ResolvesScalarsTheSameWayPsychDid(string caseJson)
    {
        var entry = JsonNode.Parse(caseJson)!.AsObject();
        var scalar = entry["scalar"]!.GetValue<string>();
        Assert.SkipWhen(KnownDivergences.ContainsKey(scalar), KnownDivergences.GetValueOrDefault(scalar) ?? "");

        var loaded = YamlLoader.LoadText($"key: {scalar}", allowAliases: false);
        var value = RubyValue.Dig(loaded, "key");

        Assert.Equal(entry["inspect"]!.GetValue<string>(), Inspect(value));
    }

    /// <summary>Renders a loaded value the way Ruby's <c>Object#inspect</c> would.</summary>
    private static string Inspect(object? value) => value switch
    {
        null => "nil",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        double number => RubyValue.ToStringValue(number),
        _ => RubyValue.ToStringValue(value),
    };
}
