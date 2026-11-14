using System.Diagnostics;

namespace Wip.Tests;

/// <summary>
/// No tracked file may start the WSL launcher by a bare name.
/// </summary>
/// <remarks>
/// <para>
/// With <c>UseShellExecute = $false</c> a bare <c>wsl.exe</c> reaches <c>CreateProcess</c> as
/// <c>lpApplicationName = null</c>, and that search order puts the current directory ahead of
/// <c>System32</c>. The rule for which launcher is trusted lives in
/// <c>scripts/Show-Wsl.ps1</c>; this test is what stops a fresh copy of the bare form from
/// appearing somewhere that file is not consulted. It was a one-off <c>grep</c> twice before
/// becoming a test, and the second time it found a site the first had not covered.
/// </para>
/// <para>
/// <b>Only the PowerShell forms are matched.</b> The C# side starts the launcher by bare name
/// too (<c>ProcessProbe</c>, reached from <c>WindowsEnvironment.DetectWsl2</c>), which is
/// tracked as #329 and fixed on its own terms -- the resolution belongs where a path is
/// resolved there, not at each call. Widening this test to the C# form would make it fail for
/// a defect it is not the fix for, which is why the forms below are listed one by one instead
/// of matching <c>wsl.exe</c> generally.
/// </para>
/// <para>
/// Matching nothing is what passing looks like here, and a broken search produces it just as
/// well as a clean tree does. <see cref="TheSearchFindsTheFormThatIsAllowed"/> therefore
/// searches for the resolved form that <c>scripts/Show-Wsl.ps1</c> certainly contains and
/// requires that file back: it proves the search runs, reads this repository, and handles the
/// <c>]::new(</c> punctuation the forbidden forms are made of.
/// </para>
/// <para>
/// <b>The forms are assembled rather than spelled out.</b> A literal spelling in this file
/// would be matched by the very search this file defines, and the way out of that -- skipping
/// this file -- would leave the one file nobody is watching unsearched. Assembling costs the
/// pieces being right instead, and <see cref="TheSearchFindsTheFormThatIsAllowed"/> checks the
/// longest of them (<see cref="Constructor"/>) by finding the allowed form built from it.
/// </para>
/// <para>
/// The git plumbing is deliberately not shared with <see cref="PrivateTrackerReferenceTests"/>.
/// Every message there is redacted, because the pattern it searches for is the one string that
/// must not reach a public log, and its helpers are built around that. This test's whole value
/// is naming the offending file and form in plain text, so inheriting the redaction would
/// defeat it.
/// </para>
/// </remarks>
public class WslLauncherLiteralTests
{
    /// <summary>The bare launcher name, PowerShell-quoted.</summary>
    private const string BareLauncher = "'wsl.exe'";

    /// <summary>The same thing spelled the way <c>Start-Process</c> and friends take it.</summary>
    private const string BareWsl = "'wsl'";

    /// <summary>
    /// The constructor call, up to its argument. Shared with <see cref="ResolvedForm"/> so that
    /// a typo here fails a test instead of quietly matching nothing.
    /// </summary>
    private const string Constructor = "ProcessStartInfo]::new(";

    /// <summary>
    /// The exact PowerShell spellings that start a launcher the working directory can supply,
    /// each with what to write instead.
    /// </summary>
    public static TheoryData<string, string> ForbiddenForms => new()
    {
        { "FileName = " + BareLauncher, "assign (Resolve-WslLauncher) from scripts/Show-Wsl.ps1" },
        { Constructor + BareLauncher + ")", "pass (Resolve-WslLauncher) to the constructor" },
        { "-FilePath " + BareWsl, "pass the resolved path, as benchmark/scripts/common.ps1 does" },
    };

    /// <summary>
    /// A form this repository does contain, holding the punctuation the forbidden forms use.
    /// </summary>
    private const string ResolvedForm = Constructor + "(Resolve-WslLauncher))";
    private const string LauncherRuleFile = "scripts/Show-Wsl.ps1";

    [Theory]
    [MemberData(nameof(ForbiddenForms))]
    public void NoTrackedFileStartsTheLauncherByBareName(string form, string instead)
    {
        // Both the index and the working tree: a commit publishes what is staged, and
        // `git commit -a` stages the working tree on the way.
        foreach (var index in new[] { true, false })
        {
            var offenders = Grep(form, index);
            Assert.True(offenders.Length == 0, string.Join("\n", [
                $"これらのファイルが `{form}` を含む。作業ディレクトリの wsl.exe が System32 より先に選ばれる。",
                $"代わりに: {instead}",
                .. offenders.Select(offender => $"  {offender}"),
            ]));
        }
    }

    /// <summary>
    /// Without this, a pattern git never matched and a search that never ran would both look
    /// exactly like a clean tree.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSearchFindsTheFormThatIsAllowed(bool index)
    {
        var found = Grep(ResolvedForm, index);
        Assert.Contains(LauncherRuleFile, found, StringComparer.Ordinal);
    }

    /// <summary>
    /// Tracked files whose content matches <paramref name="pattern"/> literally.
    /// </summary>
    /// <remarks>
    /// <c>git grep</c> exits 1 for "no match", which is the expected answer for every forbidden
    /// form, so it is tolerated here and told apart from a real failure by the exit code alone.
    /// </remarks>
    private static string[] Grep(string pattern, bool index)
    {
        string[] arguments = ["grep", .. index ? new[] { "--cached" } : [], "-l", "-z", "-F", "-e", pattern];
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
    /// Both streams are drained concurrently. Reading stdout to the end while nobody reads
    /// stderr deadlocks as soon as git writes more to stderr than the pipe buffer holds, and
    /// there is no timeout here to break it -- the same defect this test exists to keep out of
    /// the PowerShell scripts.
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
