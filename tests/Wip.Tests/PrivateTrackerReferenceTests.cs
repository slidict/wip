using System.Diagnostics;

namespace Wip.Tests;

/// <summary>
/// The private management repository's name must not appear in anything this repository
/// tracks.
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
/// The file list comes from <c>git ls-files</c> rather than from walking the directory. What
/// matters is what the repository publishes, and a working tree also holds scratch notes,
/// ignored build output and published binaries; failing <c>dotnet test</c> over a local note
/// that never ships would be a false alarm, and reading a published tree as text is pointless
/// work. Tracked content is exactly the set this is about.
/// </para>
/// <para>
/// Only the literal name is matched. A bare <c>#NNN</c> cannot be checked here, because this
/// repository's own issue references are the same shape and are legitimate -- those are caught
/// by reading, not by this test.
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

    [Fact]
    public void NoTrackedFileNamesThePrivateTracker()
    {
        var root = RepositoryRoot();
        var tracked = TrackedFiles(root);
        Assert.NotEmpty(tracked);

        var offenders = new List<string>();
        foreach (var relative in tracked)
        {
            if (Allowed.Contains(relative, StringComparer.Ordinal)) continue;
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            // A tracked path can be absent from the working tree -- a sparse checkout, or a
            // file deleted but not yet committed. Nothing to read is nothing to leak.
            if (!File.Exists(path)) continue;

            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (text.Contains(PrivateTracker, StringComparison.OrdinalIgnoreCase)) offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, string.Join("\n", [
            $"これらのファイルが非公開の管理リポジトリ名（{PrivateTracker}）を含む。",
            "公開リポジトリの読み手は辿れないため、指していた対象そのものを書く。",
            .. offenders.Select(o => $"  {o}"),
        ]));
    }

    /// <summary>
    /// Tracked paths, repository-relative with forward slashes, as git reports them.
    /// </summary>
    /// <remarks>
    /// <c>-z</c> so a path with a newline or a non-ASCII character arrives verbatim instead of
    /// quoted. The test skips rather than falls back to walking the tree: a fallback would
    /// quietly check a different set of files than the one it claims to.
    /// </remarks>
    private static IReadOnlyList<string> TrackedFiles(string root)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");

        try
        {
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git ls-files exited {process.ExitCode}");
            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Assert.Skip($"gitを実行できないため追跡ファイルの照合を省略する（{error.GetType().Name}）。");
            return [];
        }
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
