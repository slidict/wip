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
/// Both the content and the path are matched, and both come from git rather than from the
/// working tree. What gets published is what gets committed, which is the index: a file staged
/// with the name and then corrected without staging the correction still commits the name,
/// while a working-tree read of it passes. Reading the index also removes the question of what
/// to do about a tracked file that is missing or unreadable in the working tree -- a sparse
/// checkout, a file deleted but not yet committed -- because an indexed blob is always there
/// to read. Every one of those was a way to pass while covering less than this claims to.
/// </para>
/// <para>
/// Walking the directory instead would check the wrong set the other way: a working tree also
/// holds scratch notes, ignored build output and published binaries, and failing
/// <c>dotnet test</c> over a local note that never ships would be a false alarm.
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
        if (!GitOpensRepository(root)) return;

        var tracked = TrackedFiles(root);
        if (tracked is null) return;
        Assert.NotEmpty(tracked);

        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        // The path is published as well as the content, so a directory named after the tracker
        // leaks it whatever the file holds. Not covered by the content search below, which
        // reads blobs and never looks at where they live.
        foreach (var relative in tracked)
        {
            if (relative.Contains(PrivateTracker, StringComparison.OrdinalIgnoreCase)) offenders.Add(relative);
        }

        var naming = IndexedFilesNamingTheTracker(root);
        if (naming is null) return;
        foreach (var relative in naming)
        {
            if (!Allowed.Contains(relative, StringComparer.Ordinal)) offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, string.Join("\n", [
            $"これらのファイルが非公開の管理リポジトリ名（{PrivateTracker}）を含む。",
            "公開リポジトリの読み手は辿れないため、指していた対象そのものを書く。",
            .. offenders.Select(o => $"  {o}"),
        ]));
    }

    /// <summary>
    /// Whether git, run the way this test runs it, opened the repository under test. Returns
    /// <c>false</c> when git is unavailable and the test skipped.
    /// </summary>
    /// <remarks>
    /// Clearing the variables in <see cref="RepositoryLocationVariables"/> is not something a
    /// test can observe directly, and anything else that redirects git would be just as
    /// invisible. This asks git which repository it opened and compares the answer, so a
    /// redirected run fails instead of scanning a different tree and passing.
    /// </remarks>
    private static bool GitOpensRepository(string root)
    {
        var toplevel = Git(root, ["rev-parse", "--show-toplevel"]);
        if (toplevel is null) return false;

        var reported = new DirectoryInfo(toplevel.Trim()).FullName;
        Assert.True(
            string.Equals(reported, new DirectoryInfo(root).FullName, StringComparison.OrdinalIgnoreCase),
            $"gitが開いたリポジトリ（{reported}）が検査対象（{root}）と違う。");
        return true;
    }

    /// <summary>
    /// Tracked paths, repository-relative with forward slashes, as git reports them.
    /// <c>null</c> when git is unavailable and the test skipped.
    /// </summary>
    /// <remarks>
    /// <c>-z</c> so a path with a newline or a non-ASCII character arrives verbatim instead of
    /// quoted.
    /// </remarks>
    private static IReadOnlyList<string>? TrackedFiles(string root)
    {
        var output = Git(root, ["ls-files", "-z"]);
        return output?.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Indexed files whose staged content contains the tracker's name, searched by git so the
    /// bytes examined are the ones a commit would publish. <c>null</c> when git is unavailable.
    /// </summary>
    /// <remarks>
    /// <c>git grep</c> exits 1 for "no match", which is the passing case here, so only a code
    /// above 1 is a failure. Binary blobs are searched too rather than excluded: a name in one
    /// is still published.
    /// </remarks>
    private static IReadOnlyList<string>? IndexedFilesNamingTheTracker(string root)
    {
        var output = Git(root, ["grep", "--cached", "-l", "-i", "-z", "-F", "-e", PrivateTracker], allowNoMatch: true);
        return output?.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Runs git in <paramref name="root"/> and returns stdout, or <c>null</c> when git is not
    /// available (the test skips).
    /// </summary>
    /// <remarks>
    /// <c>WorkingDirectory</c> alone does not decide which repository git reads.
    /// <c>GIT_DIR</c>, <c>GIT_WORK_TREE</c> and <c>GIT_INDEX_FILE</c> override it, and an
    /// inherited one would read another repository -- a non-empty file list that contains
    /// nothing of this repository, so nothing here matches and the test passes having scanned
    /// nothing. They are removed, and the repository git actually opened is then compared
    /// against the one under test rather than assumed, since removing them is not by itself
    /// checkable.
    /// </remarks>
    private static string? Git(string root, string[] arguments, bool allowNoMatch = false)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in RepositoryLocationVariables) start.Environment.Remove(name);

        try
        {
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var tolerated = process.ExitCode == 0 || (allowNoMatch && process.ExitCode == 1);
            Assert.True(tolerated, $"git {string.Join(' ', arguments)} exited {process.ExitCode}");
            return output;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Assert.Skip($"gitを実行できないため追跡ファイルの照合を省略する（{error.GetType().Name}）。");
            return null;
        }
    }

    /// <summary>
    /// The variables that point git at a repository, index or object store other than the one
    /// <c>WorkingDirectory</c> selects.
    /// </summary>
    private static readonly string[] RepositoryLocationVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_INDEX_FILE",
        "GIT_COMMON_DIR",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_CEILING_DIRECTORIES",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM",
    ];

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
