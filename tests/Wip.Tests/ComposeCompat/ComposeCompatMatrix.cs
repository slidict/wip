using System.Text.Json.Nodes;

namespace Wip.Tests.ComposeCompat;

/// <summary>
/// tests/compose-compat/matrix.json: for each fixture, which Compose features it exercises and
/// what wip's relationship to upstream is on each of them.
/// </summary>
internal sealed class ComposeCompatMatrix
{
    /// <summary>
    /// <list type="bullet">
    /// <item><c>compatible</c> — both interpretations agree; a change on either side is a regression.</item>
    /// <item><c>known-difference</c> — both have an opinion and the opinions differ.</item>
    /// <item><c>upstream-unsupported</c> — a Compose Specification behaviour upstream rejects today.</item>
    /// <item><c>wip-only</c> — wip implements it and upstream has no notion of it at all.</item>
    /// </list>
    /// Only <c>compatible</c> is a comparison wip must keep passing; the other three record a
    /// difference so that its disappearance is noticed, and never fail for existing.
    /// </summary>
    internal static readonly string[] Statuses =
        ["compatible", "known-difference", "upstream-unsupported", "wip-only"];

    private readonly JsonObject document;

    private ComposeCompatMatrix(JsonObject document) => this.document = document;

    internal static ComposeCompatMatrix Load() =>
        new(JsonNode.Parse(File.ReadAllText(ComposeCompatCorpus.MatrixPath))!.AsObject());

    internal JsonObject Fixtures => document["fixtures"]!.AsObject();

    internal IEnumerable<string> FixtureNames() => Fixtures.Select(pair => pair.Key);

    internal JsonObject Fixture(string name) =>
        Fixtures[name]?.AsObject()
        ?? throw new InvalidOperationException($"matrix.json has no entry for fixture '{name}'");

    internal string Description(string fixture) => Fixture(fixture)["description"]!.GetValue<string>();

    internal JsonObject Features(string fixture) => Fixture(fixture)["features"]!.AsObject();

    internal string Status(string fixture, string feature) =>
        Features(fixture)[feature]!["status"]!.GetValue<string>();

    internal string Note(string fixture, string feature) =>
        Features(fixture)[feature]!["note"]!.GetValue<string>();

    /// <summary>Every (fixture, feature) pair, as xUnit theory data.</summary>
    internal static IEnumerable<object[]> Entries()
    {
        var matrix = Load();
        foreach (var fixture in matrix.FixtureNames())
        {
            foreach (var (feature, _) in matrix.Features(fixture))
            {
                yield return [fixture, feature];
            }
        }
    }
}
