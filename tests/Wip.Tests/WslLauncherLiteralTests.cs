using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Wip.Tests;

/// <summary>
/// No tracked file may start the WSL launcher by a bare name.
/// </summary>
/// <remarks>
/// <para>
/// Two different resolutions are being refused here, for the same reason. A bare name given to
/// <c>ProcessStartInfo</c> with <c>UseShellExecute = $false</c> reaches <c>CreateProcess</c> as
/// <c>lpApplicationName = null</c>, whose search order puts the current directory ahead of
/// <c>System32</c>. A bare name written as a PowerShell command is resolved through
/// <c>$env:PATH</c> instead -- the current directory does not win there, but whoever can write
/// a PATH entry still decides which launcher runs. The rule for which launcher is trusted
/// lives in <c>scripts/Show-Wsl.ps1</c>, and this test is what stops a fresh bare name from
/// appearing somewhere that file is not consulted.
/// </para>
/// <para>
/// It was a one-off <c>grep</c> twice before becoming a test, and each pass found sites the
/// previous one had missed -- the second pass missed the three command-form invocations that
/// <see cref="BenchmarkScope"/> now covers, because it searched for fixed strings that did not
/// spell them. So the patterns are regexes: quoting, spacing and the <c>.exe</c> suffix are all
/// variations of one defect and must not each need their own discovery.
/// </para>
/// <para>
/// <b>Only the PowerShell forms are matched.</b> The C# side resolves the launcher in
/// <c>WslLauncher</c>, and <c>ProcessProbe</c> and <c>CommandRunner</c> refuse any executable
/// that is not fully qualified, so a bare name there fails at the call rather than here. Every
/// pattern below is anchored to PowerShell syntax.
/// </para>
/// <para>
/// <b>The command form is searched under <c>benchmark/</c> only</b> (<see cref="BenchmarkScope"/>).
/// Repository-wide it cannot be done: <c>.github/workflows/e2e-windows.yml</c> and
/// <c>tests/e2e/run-e2e.ps1</c> legitimately quote <c>wsl --status</c> and
/// <c>wsl --update --web-download</c> inside error messages, and no regex tells an invocation
/// from prose about one. The benchmark scripts are where the defect was, and keeping prose
/// there clear of the form is a price worth one reworded comment. The quoted forms carry no
/// such ambiguity and are searched everywhere.
/// </para>
/// <para>
/// Matching nothing is what passing looks like, and a misspelled pattern produces it just as
/// well as a clean tree does. <see cref="EveryPatternMatchesTheFormItDescribes"/> is what makes
/// the difference visible: it runs each pattern against spellings it must catch and spellings
/// it must not, in process, so a typo in any piece fails loudly instead of silently disarming
/// the guard. <see cref="TheSearchReachesThisRepository"/> separately proves that
/// <c>git grep</c> runs at all and reads this tree.
/// </para>
/// <para>
/// The samples for the repository-wide patterns are interpolated rather than written out, so
/// that this file's own source does not contain the spellings it forbids. Skipping this file
/// instead would leave the one file nobody is watching unsearched.
/// </para>
/// <para>
/// The git plumbing is deliberately not shared with <see cref="PrivateTrackerReferenceTests"/>.
/// Every message there is redacted, because the pattern it searches for is the one string that
/// must not reach a public log, and its helpers are built around that. This test's whole value
/// is naming the offending file and form in plain text, so inheriting the redaction would
/// defeat it. What is copied from it deliberately is the environment scrubbing in
/// <see cref="RunGit"/>; see the note there.
/// </para>
/// </remarks>
public class WslLauncherLiteralTests
{
    /// <summary>
    /// The launcher's name, split so this file's source does not spell what it forbids. The
    /// compiler folds it; <c>git grep</c> reads the two halves.
    /// </summary>
    private const string Name = "ws" + "l";

    /// <summary>The name as a pattern, with the optional <c>.exe</c>.</summary>
    private const string NamePattern = "wsl(\\.exe)?";

    /// <summary>
    /// Horizontal whitespace. A real tab, not <c>\t</c>: the same string is handed to both
    /// <c>git grep -E</c> and .NET, and POSIX bracket expressions take <c>\t</c> literally.
    /// </summary>
    private const string Space = "[ \t]";

    /// <summary>
    /// The end of an unquoted token: horizontal whitespace, end of line, or the carriage
    /// return a CRLF checkout leaves before it.
    /// </summary>
    /// <remarks>
    /// The CR is not decoration. <c>.gitattributes</c> keeps every <c>.ps1</c> file CRLF in the
    /// working tree and LF in the index, so a pattern ending at <c>$</c> alone matches the
    /// index copy and not the file on disk. Measured: an unstaged <c>-FilePath wsl</c> was
    /// missed by the working-tree half of the search while the index half would have caught it.
    /// </remarks>
    private const string TokenEnd = "([ \t\r]|$)";

    /// <summary>Where the command form is searched. See this class's remarks for why.</summary>
    private const string BenchmarkScope = "benchmark";

    /// <summary>
    /// One forbidden spelling: what to call it, the pattern, the path it is searched under
    /// (null for the whole tree), and what to write instead.
    /// </summary>
    private sealed record Form(string What, string Pattern, string? Scope, string Instead);

    /// <summary>
    /// The spellings that let something other than the resolved launcher run. The single
    /// source for both the search and its canaries, so neither can drift from the other.
    /// </summary>
    private static readonly Form[] Forms =
    [
        new("assigned FileName",
            $"FileName{Space}*={Space}*['\"]{NamePattern}['\"]",
            null,
            "assign (Resolve-WslLauncher) from scripts/Show-Wsl.ps1"),
        new("ProcessStartInfo constructor",
            $"ProcessStartInfo\\]::new\\({Space}*['\"]{NamePattern}['\"]",
            null,
            "pass (Resolve-WslLauncher) to the constructor"),
        new("-FilePath argument",
            $"-FilePath{Space}+['\"]?{NamePattern}['\"]?{TokenEnd}",
            null,
            "pass the resolved path, as benchmark/scripts/common.ps1 does"),
        new("PowerShell command",
            $"(^|[^-A-Za-z0-9_.\\\\/]){NamePattern}{Space}+-",
            BenchmarkScope,
            "call the resolved path with & (Get-WslLauncher), or Get-WslLauncherOrNull where it must not throw"),
    ];

    public static TheoryData<string, string, string?, string> ForbiddenForms
    {
        get
        {
            var data = new TheoryData<string, string, string?, string>();
            foreach (var form in Forms) data.Add(form.What, form.Pattern, form.Scope, form.Instead);
            return data;
        }
    }

    /// <summary>Spellings each pattern has to catch, by pattern name.</summary>
    public static TheoryData<string, string> MustMatch => new()
    {
        { "assigned FileName", $"$psi.FileName = '{Name}.exe'" },
        { "assigned FileName", $"$psi.FileName=\"{Name}.exe\"" },
        { "assigned FileName", $"$psi.FileName  =  \"{Name}\"" },
        { "ProcessStartInfo constructor", $"$psi = [System.Diagnostics.ProcessStartInfo]::new('{Name}.exe')" },
        { "ProcessStartInfo constructor", $"[System.Diagnostics.ProcessStartInfo]::new( \"{Name}\" )" },
        { "-FilePath argument", $"Invoke-TimedProcess -FilePath '{Name}' -ArgumentList @()" },
        { "-FilePath argument", $"Invoke-TimedProcess -FilePath \"{Name}.exe\" -ArgumentList @()" },
        { "-FilePath argument", $"Start-Process -FilePath {Name}" },
        // With the carriage return a CRLF checkout leaves: see TokenEnd.
        { "-FilePath argument", $"Start-Process -FilePath {Name}\r" },
        { "PowerShell command", $"        {Name} --terminate $distro 2>$null | Out-Null" },
        // The suffixed spelling is the same invocation. The first version of this pattern
        // wrote the name itself instead of NamePattern, and this walked straight past it.
        { "PowerShell command", $"        {Name}.exe --terminate $distro 2>$null | Out-Null" },
        { "PowerShell command", $"$sections.Add((({Name} -d $WslDistro -- docker logs x 2>&1) -join 'n'))" },
    };

    /// <summary>
    /// Spellings no pattern may catch. The resolved forms this repository actually writes, plus
    /// the neighbours a sloppier pattern would swallow: the sibling <c>wslc</c> launcher, and
    /// another executable whose name merely ends the same way.
    /// </summary>
    public static TheoryData<string> MustNotMatch =>
    [
        "$psi.FileName = (Resolve-WslLauncher)",
        "$psi = [System.Diagnostics.ProcessStartInfo]::new((Resolve-WslLauncher))",
        "Invoke-TimedProcess -FilePath (Get-WslLauncher) -ArgumentList @()",
        "& $launcher --terminate $distro 2>$null | Out-Null",
        $"Invoke-TimedProcess -FilePath '{Name}c' -ArgumentList @()",
        "Start-Process -FilePath 'powershell.exe'",
        $"{Name}c --version",
        $"C:\\Windows\\System32\\{Name}.exe --version",
    ];

    /// <summary>
    /// A form this repository does contain, used to prove the search runs and reads this tree.
    /// </summary>
    private const string ResolvedForm = "ProcessStartInfo]::new((Resolve-WslLauncher))";
    private const string LauncherRuleFile = "scripts/Show-Wsl.ps1";

    [Theory]
    [MemberData(nameof(ForbiddenForms))]
    public void NoTrackedFileStartsTheLauncherByBareName(string what, string pattern, string? scope, string instead)
    {
        // Both the index and the working tree: a commit publishes what is staged, and
        // `git commit -a` stages the working tree on the way.
        foreach (var index in new[] { true, false })
        {
            var offenders = Grep(pattern, index, scope);
            Assert.True(offenders.Length == 0, string.Join("\n", [
                $"これらのファイルが「{what}」の形で WSL ランチャーを名前で起動している。",
                "解決済みの絶対パス以外は、作業ディレクトリか PATH を書ける者が起動対象を決められる。",
                $"代わりに: {instead}",
                .. offenders.Select(offender => $"  {offender}"),
            ]));
        }
    }

    /// <summary>
    /// Without this, a typo in any pattern piece would match nothing -- which is exactly what a
    /// clean tree looks like, so the guard would be disarmed and still green.
    /// </summary>
    [Theory]
    [MemberData(nameof(MustMatch))]
    public void EveryPatternMatchesTheFormItDescribes(string what, string sample)
    {
        var pattern = PatternFor(what);
        Assert.True(Regex.IsMatch(sample, pattern), $"「{what}」のパターンが {sample} を捕まえない: {pattern}");
    }

    [Theory]
    [MemberData(nameof(MustNotMatch))]
    public void NoPatternMatchesAResolvedOrUnrelatedForm(string sample)
    {
        foreach (var form in Forms)
        {
            Assert.False(
                Regex.IsMatch(sample, form.Pattern),
                $"「{form.What}」のパターンが {sample} を誤検出する: {form.Pattern}");
        }
    }

    private static string PatternFor(string what) => Forms.First(form => form.What == what).Pattern;

    /// <summary>
    /// Without this, a search that never ran -- a flag git stopped accepting, a repository it
    /// could not read -- would look like a clean tree too.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSearchReachesThisRepository(bool index)
    {
        var found = Grep(Regex.Escape(ResolvedForm), index, null);
        Assert.Contains(LauncherRuleFile, found, StringComparer.Ordinal);
    }

    /// <summary>
    /// Tracked files whose content matches <paramref name="pattern"/>, optionally only under
    /// <paramref name="scope"/>.
    /// </summary>
    /// <remarks>
    /// <c>git grep</c> exits 1 for "no match", which is the expected answer for every forbidden
    /// form, so it is tolerated here and told apart from a real failure by the exit code alone.
    /// </remarks>
    private static string[] Grep(string pattern, bool index, string? scope)
    {
        string[] arguments = [
            "grep",
            .. index ? new[] { "--cached" } : [],
            "-l", "-z", "-E", "-e", pattern,
            .. scope is null ? [] : new[] { "--", scope },
        ];
        var (output, error, exitCode) = RunGit(arguments);

        if (exitCode != 0 && NotARepository(error))
        {
            // A source tree with no repository in it -- a downloaded archive, a Docker stage
            // that copies the sources in -- has no tracked set to read. Anything else fails.
            Assert.Skip("gitリポジトリではないため追跡ファイルの照合を省略する。");
        }

        Assert.True(exitCode is 0 or 1, $"git grep exited {exitCode}: {Flatten(error)}");
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool NotARepository(string error) =>
        error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
        || error.Contains("this operation must be run in a work tree", StringComparison.OrdinalIgnoreCase);

    private static string Flatten(string text)
    {
        var flattened = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flattened.Length == 0 ? "(空)" : flattened;
    }

    /// <remarks>
    /// <para>
    /// Both streams are drained concurrently. Reading stdout to the end while nobody reads
    /// stderr deadlocks as soon as git writes more to stderr than the pipe buffer holds, and
    /// there is no timeout here to break it -- the same defect this test exists to keep out of
    /// the PowerShell scripts.
    /// </para>
    /// <para>
    /// The variables in <see cref="RepositoryLocationVariables"/> are removed because each of
    /// them points git at a repository, index or object store other than the one
    /// <c>WorkingDirectory</c> selects. Run the suite from a context that exports them -- a git
    /// hook, <c>git bisect run</c>, <c>git rebase --exec</c>, or
    /// <c>git submodule foreach</c> from a parent repository -- and this search would read some
    /// other tree while still reporting on this one. <see cref="TheSearchReachesThisRepository"/>
    /// usually turns that into a failure, but not always: an exported <c>GIT_DIR</c> that is
    /// invalid makes <see cref="NotARepository"/> match and every test skip instead.
    /// </para>
    /// </remarks>
    private static (string Output, string Error, int ExitCode) RunGit(string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in RepositoryLocationVariables) start.Environment.Remove(name);
        // The stderr wordings matched by NotARepository are text, so the child must not be
        // translated. Git for Windows ships catalogues and would answer in the host locale.
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
    /// Walks up to the directory holding <c>wip.slnx</c>, the shape the other repository-wide
    /// tests use to find the root.
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
