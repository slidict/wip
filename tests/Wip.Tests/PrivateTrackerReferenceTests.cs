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
/// The name is written here, exactly once, and the search is required to come back with
/// <em>this file and nothing else</em>. That is what makes the pattern itself checked: a typo
/// in <see cref="PrivateTracker"/> finds nothing, and finding nothing now fails. An earlier
/// revision assembled the name at run time so that no file contained it, which made an empty
/// result the correct answer and therefore worthless as evidence. A canary searching for some
/// other string did not fix that: it proved a search worked, not that this pattern did.
/// </para>
/// <para>
/// Nothing is allowlisted. <see cref="WikiTapeTests"/> takes the name from
/// <see cref="PrivateTracker"/> instead of spelling it out, so it stays inside the search like
/// every other file; excluding it would have left 300 lines of ordinary test code unsearched.
/// </para>
/// <para>
/// Only the literal name is matched. A bare <c>#NNN</c> cannot be checked here, because this
/// repository's own issue references are the same shape and are legitimate -- those are caught
/// by reading, not by this test.
/// </para>
/// </remarks>
public class PrivateTrackerReferenceTests
{
    internal const string PrivateTracker = "slidict/workspace";

    /// <summary>
    /// This file, as git reports it, and the one line in it that may hold the name: the
    /// declaration above. Required rather than excused, so the search has to prove it ran.
    /// </summary>
    private const string ThisFile = "tests/Wip.Tests/PrivateTrackerReferenceTests.cs";
    private const int AllowedMatchesInThisFile = 1;

    [Fact]
    public void NoTrackedFileNamesThePrivateTracker()
    {
        var root = RepositoryRoot();

        var paths = Split(Git(root, ["ls-files", "-z"]));
        Assert.NotEmpty(paths);
        // The path list has to contain this file, or it is a list of something else.
        Assert.Contains(ThisFile, paths);

        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relative in paths)
        {
            if (relative.Contains(PrivateTracker, StringComparison.OrdinalIgnoreCase)) offenders.Add(relative);
        }

        foreach (var index in new[] { true, false })
        {
            var naming = Split(Grep(root, index, ["-l"]));
            // This file must be there: it declares the name. Anything else named it.
            Assert.Contains(ThisFile, naming);
            offenders.UnionWith(naming.Where(path => path != ThisFile));
        }

        Assert.True(offenders.Count == 0, string.Join("\n", [
            "これらのファイルが非公開の管理リポジトリ名を含む。",
            "公開リポジトリの読み手は辿れないため、指していた対象そのものを書く。",
            .. offenders.Select(o => $"  {o}"),
        ]));

        foreach (var index in new[] { true, false })
        {
            var matches = CountIn(root, index, ThisFile);
            Assert.True(matches == AllowedMatchesInThisFile, string.Join("\n", [
                $"{ThisFile} が管理リポジトリ名を {matches} 行で含む（宣言の {AllowedMatchesInThisFile} 行だけが許される）。",
                index ? "対象: index" : "対象: 作業ツリー",
            ]));
        }
    }

    /// <summary>
    /// Runs the content search over the index or the working tree.
    /// </summary>
    /// <remarks>
    /// <c>git grep</c> exits 1 for "no match". That is tolerated only so the callers can assert
    /// on the result; for them an empty result is a failure, not a pass. Binary blobs are
    /// searched too rather than excluded: a name in one is published just the same.
    /// </remarks>
    private static string Grep(string root, bool index, string[] options, string? path = null)
    {
        // git refuses an option after a non-option argument, so the pathspec goes last, after
        // the pattern, behind its own `--`.
        string[] arguments = [
            "grep", .. index ? new[] { "--cached" } : [], .. options, "-i", "-z", "-F", "-e", PrivateTracker,
            .. path is null ? Array.Empty<string>() : ["--", path],
        ];
        return Git(root, arguments, allowNoMatch: true);
    }

    /// <summary>
    /// How many lines of <paramref name="path"/> name the tracker. Measured: <c>grep -c -z</c>
    /// writes one record per file as the path, a NUL, the count and a newline, so restricting it
    /// to a single path keeps the output to one record and two fields.
    /// </summary>
    private static int CountIn(string root, bool index, string path)
    {
        var fields = Split(Grep(root, index, ["-c"], path));
        if (fields.Length != 2) return 0;
        return int.TryParse(fields[1].Trim(), out var count) ? count : 0;
    }

    private static string[] Split(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Runs git in <paramref name="root"/> and returns stdout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both streams are drained concurrently. Reading stdout to the end while nobody reads
    /// stderr deadlocks as soon as git writes more to stderr than the pipe buffer holds -- an
    /// advice block or a pack warning is enough -- and there is no timeout to break it.
    /// </para>
    /// <para>
    /// A missing git binary fails rather than skips. Skipping would remove the only guard
    /// against publishing the name, and remove it silently: the run would stay green with
    /// nothing checked. The one case that does skip is a source tree with no repository in it
    /// -- a downloaded archive, a Docker stage that copies sources in -- where there is no
    /// tracked set to read at all. A redirected repository does not reach that path: it answers
    /// normally, and the assertions above catch it, because they require this file back.
    /// </para>
    /// <para>
    /// Failure messages name the subcommand but never the arguments, and redact git's own
    /// stderr. The pattern is the tracker's name, and these messages end up in a public CI log.
    /// </para>
    /// </remarks>
    private static string Git(string root, string[] arguments, bool allowNoMatch = false)
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
        // The messages below are matched as text, so the child must not be translated. Git for
        // Windows ships translation catalogues and would otherwise answer in the host locale.
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";

        var subcommand = arguments.Length > 0 ? arguments[0] : "(none)";
        string output;
        string error;
        int exitCode;
        using (var process = Process.Start(start))
        {
            Assert.NotNull(process);
            var readOutput = process.StandardOutput.ReadToEndAsync();
            var readError = process.StandardError.ReadToEndAsync();
            output = readOutput.GetAwaiter().GetResult();
            error = readError.GetAwaiter().GetResult();
            process.WaitForExit();
            exitCode = process.ExitCode;
        }

        if (exitCode != 0 && NotARepository(error))
        {
            Assert.Skip("gitリポジトリではないため追跡ファイルの照合を省略する。");
        }

        var tolerated = exitCode == 0 || (allowNoMatch && exitCode == 1);
        Assert.True(tolerated, $"git {subcommand} exited {exitCode}: {Redact(error)}");
        return output;
    }

    /// <summary>
    /// What git says on stderr when there is no repository to read, in the wordings it has used
    /// across versions. Matched on stderr rather than on the exit code, which is 128 for every
    /// fatal -- including the ones that must not be mistaken for an absent repository.
    /// </summary>
    private static bool NotARepository(string error) =>
        error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
        || error.Contains("this operation must be run in a work tree", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// git's own stderr can quote the pattern back -- a usage error does -- so the name is taken
    /// out before the text reaches a public log.
    /// </summary>
    private static string Redact(string error)
    {
        var text = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = text.Replace(PrivateTracker, "<redacted>", StringComparison.OrdinalIgnoreCase);
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

