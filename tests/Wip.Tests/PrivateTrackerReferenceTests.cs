using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

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
/// The expected result is that nothing matches, which on its own is worthless as evidence: a
/// pattern that is wrong, and a search that is not running, both produce it. So two things are
/// proved separately. <see cref="PatternIsTheTrackerName"/> hashes the pattern and compares the
/// digest, which catches a typo without writing the name into a tracked file.
/// <see cref="SearchReachesThisRepository"/> runs the same search for a string this repository
/// certainly contains and requires the file holding it back, which catches a search that
/// matches nothing and a run that is reading some other repository -- <c>GIT_DIR</c> and
/// friends override <c>WorkingDirectory</c>.
/// </para>
/// <para>
/// <b>The name is deliberately not written out here.</b> An earlier revision spelled it into
/// the constant so the search would find its own declaration, which did prove the pattern --
/// by putting the exact string this test exists to keep out of the repository into a tracked,
/// published file. Hashing proves the same thing without publishing it. Do not inline it.
/// </para>
/// <para>
/// Nothing is allowlisted either. <see cref="WikiTapeTests"/> takes the name from
/// <see cref="PrivateTracker"/> rather than spelling it out, so it stays inside the search like
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
    /// <summary>
    /// The private tracker's name, assembled so that no tracked file in this public repository
    /// contains it. Verified by <see cref="PatternIsTheTrackerName"/>.
    /// </summary>
    internal static readonly string PrivateTracker = string.Join('/', "slidict", "workspace");

    /// <summary>
    /// SHA-256 of <see cref="PrivateTracker"/>. A digest is not the name: it pins the pattern
    /// without disclosing it to a reader of this public file.
    /// </summary>
    private const string PatternDigest = "e4c06a3a20b44c903e50773f56d02011b978d64eb2653a9b8fc0afaa04415b1b";

    /// <summary>
    /// A string this repository certainly contains, used to prove the search runs and reads
    /// this repository, and the file it has to come back with.
    /// </summary>
    private const string KnownNeedle = nameof(PrivateTrackerReferenceTests);
    private const string ThisFile = "tests/Wip.Tests/PrivateTrackerReferenceTests.cs";

    /// <summary>
    /// Without this, a misspelled pattern would match nothing, and matching nothing is what
    /// passing looks like.
    /// </summary>
    [Fact]
    public void PatternIsTheTrackerName()
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PrivateTracker)));
        Assert.Equal(PatternDigest, digest);
    }

    /// <summary>
    /// Without this, a search that silently matches nothing -- a flag git stopped accepting, a
    /// redirected repository -- would also look like passing.
    /// </summary>
    [Fact]
    public void SearchReachesThisRepository()
    {
        var root = RepositoryRoot();
        Assert.Contains(ThisFile, Split(Git(root, ["ls-files", "-z"])));
        foreach (var index in new[] { true, false })
        {
            Assert.Contains(ThisFile, Split(Grep(root, KnownNeedle, index)));
        }
    }

    [Fact]
    public void NoTrackedFileNamesThePrivateTracker()
    {
        var root = RepositoryRoot();

        var paths = Split(Git(root, ["ls-files", "-z"]));
        Assert.NotEmpty(paths);

        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        // A content search reads blobs and never looks at where they live.
        foreach (var relative in paths)
        {
            if (relative.Contains(PrivateTracker, StringComparison.OrdinalIgnoreCase)) offenders.Add(relative);
        }
        foreach (var index in new[] { true, false })
        {
            offenders.UnionWith(Split(Grep(root, PrivateTracker, index)));
        }

        Assert.True(offenders.Count == 0, string.Join("\n", [
            "これらのファイルが非公開の管理リポジトリ名を含む。",
            "公開リポジトリの読み手は辿れないため、指していた対象そのものを書く。",
            // Redacted: a path can be the thing that names the tracker, and this message
            // lands in a public CI log.
            .. offenders.Select(o => $"  {Redact(o)}"),
        ]));
    }

    /// <summary>
    /// Tracked files whose content matches, from the index or from the working tree.
    /// </summary>
    /// <remarks>
    /// <c>git grep</c> exits 1 for "no match". That is tolerated here because the callers decide
    /// what an empty result means -- nothing for the tracker, a failure for the known needle.
    /// Binary blobs are searched too rather than excluded: a name in one is published just the
    /// same.
    /// </remarks>
    private static string Grep(string root, string pattern, bool index) =>
        Git(root, ["grep", .. index ? new[] { "--cached" } : [], "-l", "-i", "-z", "-F", "-e", pattern],
            allowNoMatch: true);

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
    /// nothing checked. The one case that skips is a source tree with no repository in it -- a
    /// downloaded archive, a Docker stage that copies sources in -- where there is no tracked
    /// set to read at all. It is decided on the first call, by
    /// <see cref="RepositoryIsReadable"/>, rather than on any call: deciding it late would let
    /// a later failure discard findings the earlier calls had already collected.
    /// </para>
    /// <para>
    /// Failure messages name the subcommand but never the arguments, and redact git's own
    /// stderr. The pattern is the tracker's name, and these messages end up in a public CI log.
    /// </para>
    /// </remarks>
    private static string Git(string root, string[] arguments, bool allowNoMatch = false)
    {
        var subcommand = arguments.Length > 0 ? arguments[0] : "(none)";
        var (output, error, exitCode) = RunGit(root, arguments);

        if (exitCode != 0 && NotARepository(error))
        {
            // Only reachable before the first assertion: RepositoryIsReadable has already
            // skipped when there is no repository, so reaching it here means git stopped
            // seeing one mid-run, which is a failure and not a reason to drop findings.
            Assert.Fail($"git {subcommand} lost the repository: {Redact(error)}");
        }

        var tolerated = exitCode == 0 || (allowNoMatch && exitCode == 1);
        Assert.True(tolerated, $"git {subcommand} exited {exitCode}: {Redact(error)}");
        return output;
    }

    /// <summary>
    /// Decided once, before anything is collected, so that a repository that is simply not
    /// there skips while anything else fails.
    /// </summary>
    /// <remarks>
    /// Lazy rather than a field initialiser. Static fields initialise in declaration order, and
    /// running git needs <see cref="RepositoryLocationVariables"/>, which is declared below --
    /// so computing this eagerly dereferenced a null array and surfaced as a
    /// <c>TypeInitializationException</c> in every test touching this class.
    /// </remarks>
    private static readonly Lazy<bool> ReadableRepository = new(CheckRepository);

    private static bool RepositoryIsReadable => ReadableRepository.Value;

    private static bool CheckRepository()
    {
        var (_, error, exitCode) = RunGitRaw(RepositoryRoot(), ["rev-parse", "--git-dir"]);
        return exitCode == 0 || !NotARepository(error);
    }

    /// <summary>
    /// Runs git with the guard applied. Separate from <see cref="RunGitRaw"/> because the guard
    /// is itself computed by running git: routing that first call through the guard would have
    /// it read its own uninitialised field, and asserting from a static initialiser surfaces as
    /// a <c>TypeInitializationException</c> in every test that touches this class --
    /// <see cref="WikiTapeTests"/> included, since it reads <see cref="PrivateTracker"/>.
    /// </summary>
    private static (string Output, string Error, int ExitCode) RunGit(string root, string[] arguments)
    {
        if (!RepositoryIsReadable)
        {
            Assert.Skip("gitリポジトリではないため追跡ファイルの照合を省略する。");
        }

        return RunGitRaw(root, arguments);
    }

    private static (string Output, string Error, int ExitCode) RunGitRaw(string root, string[] arguments)
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

        using var process = Process.Start(start);
        Assert.NotNull(process);
        var readOutput = process.StandardOutput.ReadToEndAsync();
        var readError = process.StandardError.ReadToEndAsync();
        var output = readOutput.GetAwaiter().GetResult();
        var error = readError.GetAwaiter().GetResult();
        process.WaitForExit();
        return (output, error, process.ExitCode);
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
    /// Takes the name out of text on its way to a public log. git's own stderr can quote the
    /// pattern back -- a usage error does -- and an offending path can contain it outright.
    /// </summary>
    private static string Redact(string text)
    {
        var flattened = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        flattened = flattened.Replace(PrivateTracker, "<redacted>", StringComparison.OrdinalIgnoreCase);
        return flattened.Length == 0 ? "(空)" : flattened;
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
