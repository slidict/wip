using System.Text.Json.Nodes;

namespace Wip.Tests.ComposeCompat;

/// <summary>
/// Locates the shared Compose fixtures under <c>tests/compose-compat</c> and the recorded
/// interpretations kept beside them. See tests/compose-compat/README.md.
/// </summary>
internal static class ComposeCompatCorpus
{
    /// <summary>
    /// Set to rewrite <c>expected.json</c> and <c>differences.md</c> from the current
    /// interpreters instead of comparing against them. Every comparison test skips while it
    /// is set, so one run either records or checks, never half of each.
    /// </summary>
    internal const string UpdateVariable = "WIP_COMPOSE_COMPAT_UPDATE";

    /// <summary>Stands in for the fixture directory, which differs per checkout and per OS.</summary>
    internal const string FixturePlaceholder = "<FIXTURE>";

    internal static string Root { get; } = FindRoot();

    internal static string FixturesDirectory => Path.Combine(Root, "fixtures");

    internal static string MatrixPath => Path.Combine(Root, "matrix.json");

    internal static string DifferencesPath => Path.Combine(Root, "differences.md");

    internal static bool UpdateMode =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(UpdateVariable));

    internal static IEnumerable<string> FixtureNames() =>
        Directory.EnumerateDirectories(FixturesDirectory).Select(Path.GetFileName).OfType<string>().Order();

    /// <summary>Fixture names as xUnit theory data — one case per fixture.</summary>
    internal static IEnumerable<object[]> Fixtures() => FixtureNames().Select(name => new object[] { name });

    internal static string DirectoryOf(string fixture) => Path.Combine(FixturesDirectory, fixture);

    internal static string ComposePath(string fixture) => Path.Combine(DirectoryOf(fixture), "compose.yml");

    internal static string ExpectedPath(string fixture) => Path.Combine(DirectoryOf(fixture), "expected.json");

    internal static JsonObject Expected(string fixture) =>
        JsonNode.Parse(File.ReadAllText(ExpectedPath(fixture)))!.AsObject();

    /// <summary>
    /// Rewrites the fixture's own absolute path to <see cref="FixturePlaceholder"/>, in the
    /// forward-slash spelling both interpreters emit, so a recording made on Linux compares
    /// byte for byte against one made on Windows.
    /// </summary>
    internal static string Anonymize(string value, string fixture)
    {
        var native = DirectoryOf(fixture).TrimEnd('/', '\\');
        var forward = native.Replace('\\', '/');
        return value
            .Replace(native, FixturePlaceholder, StringComparison.OrdinalIgnoreCase)
            .Replace(forward, FixturePlaceholder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pretty-printed with a trailing newline, so the recordings read as source files. The
    /// relaxed encoder is what keeps &lt;FIXTURE&gt; spelled that way in the file rather than
    /// as \u003C; comparisons re-serialize both sides with the default encoder, so the choice
    /// here only affects readability.
    /// </summary>
    internal static string Render(JsonNode? node) =>
        (node?.ToJsonString(RenderOptions) ?? "null") + "\n";

    private static readonly System.Text.Json.JsonSerializerOptions RenderOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "compose-compat");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find tests/compose-compat walking up from {AppContext.BaseDirectory}");
    }
}
