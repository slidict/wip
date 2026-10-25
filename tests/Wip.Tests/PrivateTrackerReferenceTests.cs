using System.Diagnostics;

namespace Wip.Tests;

/// <summary>
/// The private management repository's name must not appear in anything this repository
/// publishes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WikiTapeTests"/> already refuses it, but only inside <c>docs/tapes</c>, because
/// that is what the wiki links to as raw URLs. That is where the check was, and the references
/// were everywhere else: the volume contract, the lifecycle docs, two XML doc comments, a
/// benchmark script's synopsis. None of them is under <c>docs/tapes</c>, so nothing ever
/// failed. A guard whose scope excludes where the problem lives is not a guard, and every
/// revision of this file has been about closing another way for it to pass without looking.
/// </para>
/// <para>
/// Content is searched with <c>git grep</c> over both the index and the working tree, and
/// paths over <c>git ls-files</c>. The index because a commit publishes what is staged; the
/// working tree because <c>git commit -a</c> and <c>git commit &lt;path&gt;</c> stage it on the
/// way; and the paths because a directory named after the tracker leaks it whatever its files
/// hold, which a content search never sees.
/// </para>
/// <para>
/// A content search that finds nothing is indistinguishable from one that is not working, so
/// <see cref="SearchFindsThisFile"/> runs the same search for a string this file certainly
/// contains and requires it back. That also settles which repository git opened: a redirected
/// run -- <c>GIT_DIR</c> and friends override <c>WorkingDirectory</c> -- reads a tree that does
/// not contain this file, and the canary fails instead of the whole test passing over someone
/// else's files. Comparing paths was the previous approach and it was worse: it failed on a
/// checkout reached through a symlink, where git reports the physical path and .NET does not.
/// </para>
/// <para>
/// Nothing allowlists a file, because no file spells the name: <see cref="PrivateTracker"/>
/// assembles it at run time and <see cref="WikiTapeTests"/> uses that. An allowlist would have
/// excluded two real files from the search -- one of them 300 lines of ordinary test code --
/// and a reference added anywhere in them would have stayed green. **Do not inline it back into
/// a literal.**
/// </para>
/// <para>
/// Only the literal name is matched. A bare <c>#NNN</c> cannot be checked here, because this
/// repository's own issue references are the same shape and are legitimate -- those are caught
/// by reading, not by this test.
/// </para>
/// </remarks>
public class PrivateTrackerReferenceTests
{
    /// <summary>
    /// The private tracker's name, assembled rather than written, so that no file in this
    /// repository contains the string this test searches for. Spelling it out would put it in
    /// a tracked file and force an allowlist, and an allowlisted file is an unsearched file.
    /// </summary>
    internal static readonly string PrivateTracker = string.Join('/', "slidict", "workspace");

    /// <summary>
    /// This file, as git reports it. The canary searches for its own class name and requires
    /// this path back.
    /// </summary>
    private const string ThisFile = "tests/Wip.Tests/PrivateTrackerReferenceTests.cs";

    [Fact]
    public void NoTrackedFileNamesThePrivateTracker()
    {
        var root = RepositoryRoot();

        var tracked = Git(root, ["ls-files", "-z"]);
        if (tracked is null) return;
        var paths = Split(tracked.Output);
        Assert.NotEmpty(paths);

        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        // A content search reads blobs and never looks at where they live.
        foreach (var relative in paths)
        {
            if (relative.Contains(PrivateTracker, StringComparison.OrdinalIgnoreCase)) offenders.Add(relative);
        }

        var staged = Grep(root, PrivateTracker, index: true);
        var working = Grep(root, PrivateTracker, index: false);
        if (staged is null || working is null) return;
        offenders.UnionWith(staged);
        offenders.UnionWith(working);

        Assert.True(offenders.Count == 0, string.Join("\n", [
            $"これらのファイルが非公開の管理リポジトリ名（{PrivateTracker}）を含む。",
            "公開リポジトリの読み手は辿れないため、指していた対象そのものを書く。",
            .. offenders.Select(o => $"  {o}"),
        ]));
    }

    /// <summary>
    /// The canary. An empty result from the search above is the passing case, which means a
    /// search that silently matches nothing -- a typo in the pattern, a flag git stopped
    /// accepting, a redirected repository -- would pass too. This searches for a string that is
    /// certainly in this repository and requires the file holding it back.
    /// </summary>
    [Fact]
    public void SearchFindsThisFile()
    {
        var root = RepositoryRoot();
        var needle = nameof(PrivateTrackerReferenceTests);

        var staged = Grep(root, needle, index: true);
        if (staged is null) return;
        Assert.Contains(ThisFile, staged);

        var working = Grep(root, needle, index: false);
        if (working is null) return;
        Assert.Contains(ThisFile, working);
    }

    /// <summary>
    /// Tracked files whose content contains <paramref name="pattern"/>, from the index when
    /// <paramref name="index"/> is set and from the working tree otherwise. <c>null</c> when
    /// the test skipped.
    /// </summary>
    /// <remarks>
    /// <c>git grep</c> exits 1 for "no match", which is a passing case here, so only a code
    /// above 1 is a failure. Binary blobs are searched too rather than excluded: a name in one
    /// is published just the same.
    /// </remarks>
    private static IReadOnlyList<string>? Grep(string root, string pattern, bool index)
    {
        string[] arguments = index
            ? ["grep", "--cached", "-l", "-i", "-z", "-F", "-e", pattern]
            : ["grep", "-l", "-i", "-z", "-F", "-e", pattern];
        var result = Git(root, arguments, allowNoMatch: true);
        return result is null ? null : Split(result.Output);
    }

    private static string[] Split(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private sealed record GitResult(string Output, string Error);

    /// <summary>
    /// Runs git in <paramref name="root"/>, or returns <c>null</c> after skipping the test when
    /// git cannot be used here at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both streams are drained concurrently. Reading stdout to the end while nobody reads
    /// stderr deadlocks as soon as git writes more to stderr than the pipe buffer holds -- an
    /// advice block or a pack warning is enough -- and there is no timeout to break it.
    /// stderr is also what says why a failure happened, so the assertions carry it.
    /// </para>
    /// <para>
    /// Two cases skip rather than fail: git is not installed, and this is not a git repository.
    /// The second is a source tree without history -- a downloaded archive, a Docker stage that
    /// copies sources in -- where the tracked set is not knowable, which is different from a
    /// tracked set that cannot be read. A redirected repository does not reach this path: it
    /// answers normally, and <see cref="SearchFindsThisFile"/> is what catches it.
    /// </para>
    /// </remarks>
    private static GitResult? Git(string root, string[] arguments, bool allowNoMatch = false)
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

        string output;
        string error;
        int exitCode;
        try
        {
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var readOutput = process.StandardOutput.ReadToEndAsync();
            var readError = process.StandardError.ReadToEndAsync();
            output = readOutput.GetAwaiter().GetResult();
            error = readError.GetAwaiter().GetResult();
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Assert.Skip($"gitを実行できないため追跡ファイルの照合を省略する（{failure.GetType().Name}）。");
            return null;
        }

        if (exitCode != 0 && NotARepository(error))
        {
            Assert.Skip($"gitリポジトリではないため追跡ファイルの照合を省略する（{Trim(error)}）。");
            return null;
        }

        var tolerated = exitCode == 0 || (allowNoMatch && exitCode == 1);
        Assert.True(tolerated, $"git {string.Join(' ', arguments)} exited {exitCode}: {Trim(error)}");
        return new GitResult(output, error);
    }

    /// <summary>
    /// git reports this on stderr when it cannot find a repository, in several wordings across
    /// versions. Matched on stderr rather than on the exit code, which is 128 for every fatal.
    /// </summary>
    private static bool NotARepository(string error) =>
        error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
        || error.Contains("this operation must be run in a work tree", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string error)
    {
        var text = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length == 0 ? "(stderrは空)" : text;
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
