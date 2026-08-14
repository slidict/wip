using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wip.Tests;

/// <summary>
/// Locates and loads the fixtures under tests/golden. See tests/golden/README.md for what
/// they pin and which expectations are allowed to change.
/// </summary>
internal static class GoldenCorpus
{
    internal static string Root { get; } = FindRoot();

    internal static string CasesDirectory => Path.Combine(Root, "cases");

    internal static string UnitsDirectory => Path.Combine(Root, "units");

    internal static JsonNode LoadUnit(string name)
    {
        var path = Path.Combine(UnitsDirectory, $"{name}.json");
        return JsonNode.Parse(File.ReadAllText(path))
               ?? throw new InvalidOperationException($"{path} parsed as JSON null");
    }

    /// <summary>
    /// Each case is handed to the test as raw JSON text: xUnit serializes theory data
    /// between discovery and execution, and a string survives that round trip where a
    /// JsonNode would not.
    /// </summary>
    internal static IEnumerable<object[]> UnitCases(string name, string? arrayProperty = "cases")
    {
        var loaded = LoadUnit(name);
        var array = (arrayProperty is null ? loaded : loaded[arrayProperty])!.AsArray();
        return array.Select(entry => new object[] { entry!.ToJsonString() });
    }

    internal static IEnumerable<string> FixtureNames() =>
        Directory.EnumerateDirectories(CasesDirectory).Select(Path.GetFileName).OfType<string>().Order();

    /// <summary>
    /// Yields (fixture, operation, expected-json) triples for every recorded operation.
    /// </summary>
    internal static IEnumerable<object[]> FixtureOperations()
    {
        foreach (var fixture in FixtureNames())
        {
            var path = Path.Combine(CasesDirectory, fixture, "cases.json");
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (var (operation, expected) in document)
            {
                yield return [fixture, operation, expected!.ToJsonString()];
            }
        }
    }

    /// <summary>
    /// The generator rewrote the fixture's own absolute path to &lt;FIXTURE&gt;; put it back
    /// so expectations can be compared against values produced from a real directory.
    /// </summary>
    internal static string Resolve(string expected, string fixture) =>
        expected.Replace("<FIXTURE>", Path.Combine(CasesDirectory, fixture));

    internal static JsonSerializerOptions Options { get; } = new() { WriteIndented = false };

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "golden");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find tests/golden walking up from {AppContext.BaseDirectory}");
    }
}
