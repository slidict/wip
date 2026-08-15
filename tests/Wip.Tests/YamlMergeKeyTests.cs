using System.Text.Json.Nodes;
using Wip.Yaml;

namespace Wip.Tests;

/// <summary>
/// YAML merge keys, checked against what Psych actually produced.
/// </summary>
/// <remarks>
/// Found by running the built binary against a real compose.yml, which used
/// <c>&lt;&lt;: *anchor</c> and was rejected with "unsupported key(s): &lt;&lt;". Psych resolves
/// merge keys transparently, so the Ruby build never saw the key at all; reading the document
/// through the parser's own events does see it, and had to learn to fold it in. None of the
/// original fixtures used a merge key, which is why the corpus did not catch this.
/// </remarks>
public class YamlMergeKeyTests
{
    public static IEnumerable<object[]> Cases() => GoldenCorpus.UnitCases("yaml_merge_keys", arrayProperty: null);

    [Theory]
    [MemberData(nameof(Cases))]
    public void ResolvesMergeKeysTheSameWayPsychDid(string caseJson)
    {
        var entry = JsonNode.Parse(caseJson)!.AsObject();
        var loaded = YamlLoader.LoadText(entry["yaml"]!.GetValue<string>(), allowAliases: true);

        Assert.Equal(
            entry["expected"]!.ToJsonString(),
            RubyJson.Canonical(RubyJson.ToJson(loaded)));
    }

    /// <summary>
    /// A merge through an alias needs aliases enabled, so in wip.yml — where they are refused
    /// — it stays refused rather than quietly resolving.
    /// </summary>
    [Fact]
    public void MergeThroughAnAliasStillNeedsAliasesEnabled()
    {
        var exception = Assert.Throws<ConfigException>(() =>
            YamlLoader.LoadText("base: &b {x: 1}\napp:\n  <<: *b\n", allowAliases: false));

        Assert.Contains("aliases are not allowed", exception.Message);
    }
}
