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
/// Both the path and the content are matched, and a tracked file that cannot be read is
/// reported rather than skipped. Every one of those was a way to pass while covering less than
/// the set above -- a directory named after the tracker, a file missing from the working tree,
/// a read that threw -- which is the same hole as the one in the first paragraph.
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
        var uninspected = new List<string>();
        foreach (var relative in tracked)
        {
            // The path is published too, so a directory named after the tracker leaks it
            // whatever the file contains. Checked before reading, because this one holds for a
            // path that is not in the working tree at all.
            if (relative.Contains(PrivateTracker, StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(relative);
                continue;
            }
            if (Allowed.Contains(relative, StringComparer.Ordinal)) continue;

            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            // A tracked file this cannot read is a tracked file this did not check, and the
            // whole point is to cover everything the repository publishes. Skipping it would
            // pass while leaving the content unexamined -- the same shape of hole the test
            // exists to close -- so it is reported instead.
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                uninspected.Add($"{relative} ({error.GetType().Name})");
                continue;
            }
            if (text.Contains(PrivateTracker, StringComparison.OrdinalIgnoreCase)) offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, string.Join("\n", [
            $"これらのファイルが非公開の管理リポジトリ名（{PrivateTracker}）を含む。",
            "公開リポジトリの読み手は辿れないため、指していた対象そのものを書く。",
            .. offenders.Select(o => $"  {o}"),
        ]));
        Assert.True(uninspected.Count == 0, string.Join("\n", [
            "これらの追跡ファイルを読めなかったため、照合できていない。",
            "読めないものを通すと、検査したつもりで中身を見ていない状態になる。",
            .. uninspected.Select(o => $"  {o}"),
        ]));
    }

    /// <summary>
    /// Tracked paths, repository-relative with forward slashes, as git reports them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-z</c> so a path with a newline or a non-ASCII character arrives verbatim instead of
    /// quoted. The test skips rather than falls back to walking the tree: a fallback would
    /// quietly check a different set of files than the one it claims to.
    /// </para>
    /// <para>
    /// <c>WorkingDirectory</c> alone does not decide which repository git reads.
    /// <c>GIT_DIR</c>, <c>GIT_WORK_TREE</c> and <c>GIT_INDEX_FILE</c> override it, and an
    /// inherited one would list another repository's files -- a non-empty list that contains
    /// nothing of this repository, so every path misses here and the test passes having
    /// scanned nothing. They are removed, and then the repository git actually opened is
    /// compared to the one being tested rather than assumed.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> TrackedFiles(string root)
    {
        var toplevel = Git(root, ["rev-parse", "--show-toplevel"]);
        if (toplevel is null) return [];
        var reported = new DirectoryInfo(toplevel.Trim()).FullName;
        Assert.True(
            string.Equals(reported, new DirectoryInfo(root).FullName, StringComparison.OrdinalIgnoreCase),
            $"gitが開いたリポジトリ（{reported}）が検査対象（{root}）と違う。");

        var output = Git(root, ["ls-files", "-z"]);
        if (output is null) return [];
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Runs git in <paramref name="root"/> and returns stdout, or <c>null</c> when git is not
    /// available (the test skips).
    /// </summary>
    private static string? Git(string root, string[] arguments)
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
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} exited {process.ExitCode}");
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
