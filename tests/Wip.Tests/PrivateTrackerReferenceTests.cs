using System.Text.RegularExpressions;

namespace Wip.Tests;

/// <summary>
/// The private management repository's name must not appear anywhere this repository ships.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WikiTapeTests"/> already refuses it, but only inside <c>docs/tapes</c>, because
/// that is what the wiki links to as raw URLs. That is where the check was, and the references
/// were everywhere else: the volume contract, the lifecycle docs, two XML doc comments, a
/// benchmark script's synopsis. None of them is under <c>docs/tapes</c>, so nothing ever
/// failed. A guard whose scope excludes where the problem lives is not a guard.
/// </para>
/// <para>
/// This one reads the tracked tree instead. The name is never legitimate in published content:
/// a reader of a public repository cannot open it, and it discloses how the work is tracked
/// privately. The allowlist holds the two places that must name it in order to forbid it.
/// </para>
/// </remarks>
public class PrivateTrackerReferenceTests
{
    private const string PrivateTracker = "slidict/workspace";

    /// <summary>
    /// The files whose job is to name it. Both state the rule rather than depending on it, so
    /// excluding them is not a hole: anything else naming it is the thing being prevented.
    /// </summary>
    private static readonly string[] Allowed =
    [
        "tests/Wip.Tests/WikiTapeTests.cs",
        "tests/Wip.Tests/PrivateTrackerReferenceTests.cs",
    ];

    /// <summary>
    /// Directories that are build output or vendored content rather than something this
    /// repository publishes. Matching the path separator both ways keeps this working on
    /// Windows and Linux, since the suite runs on both.
    /// </summary>
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "node_modules", "TestResults"];

    [Fact]
    public void NoTrackedFileNamesThePrivateTracker()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (SkippedDirectories.Any(d => relative.Split('/').Contains(d, StringComparer.Ordinal))) continue;
            if (Allowed.Contains(relative, StringComparer.Ordinal)) continue;
            // Binary assets have no prose to leak; reading them as text would only produce
            // noise, and the GIFs beside the tapes are the reason this matters.
            if (IsBinary(relative)) continue;

            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }
            if (text.Contains(PrivateTracker, StringComparison.OrdinalIgnoreCase)) offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, string.Join("\n", [
            $"これらのファイルが非公開の管理リポジトリ名（{PrivateTracker}）を含む。",
            "公開リポジトリの読み手は辿れないため、指していた対象そのものを書く。",
            .. offenders.Select(o => $"  {o}"),
        ]));
    }

    private static bool IsBinary(string relative)
    {
        var extension = Path.GetExtension(relative).ToLowerInvariant();
        return extension is ".gif" or ".png" or ".jpg" or ".jpeg" or ".ico" or ".pdf"
            or ".dll" or ".exe" or ".pdb" or ".zip" or ".gz" or ".nupkg";
    }

    /// <summary>
    /// Walks up to the directory holding <c>wip.slnx</c>, the same shape
    /// <see cref="WikiTapeTests"/> uses to find <c>docs/tapes</c>.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "wip.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find wip.slnx walking up from {AppContext.BaseDirectory}");
    }
}
