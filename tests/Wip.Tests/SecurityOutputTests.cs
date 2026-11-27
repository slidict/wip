using System.CommandLine;
using System.Text;
using System.Diagnostics;
using Wip.Cli;
using Wip.Diagnostics;
using Wip.Execution;
using Wip.Security;

namespace Wip.Tests;

[Collection(ConsoleStateCollection.Name)]
public sealed class SecurityOutputTests
{
    [Fact]
    public void RepeatedToolSelectionIsRejected()
    {
        var parsed = Program.Parse(Program.BuildRoot(),
            ["security", "scan", "scanner", "--tool", "zap", "--tool", "nuclei", "--target", "http://app/"]);
        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void SecurityScanParsesOutputDirectoryAndCommitWithoutChangingTarget()
    {
        var parsed = Program.Parse(Program.BuildRoot(),
            ["security", "scan", "scanner", "--tool", "zap", "--target", "http://app:8080/",
             "--output", "artifacts/security results", "--commit", "deadbeef"]);
        Assert.Empty(parsed.Errors);
        var command = parsed.CommandResult.Command;
        var output = Assert.IsType<Option<string?>>(command.Options.Single(option => option.Name == "--output"));
        var commit = Assert.IsType<Option<string?>>(command.Options.Single(option => option.Name == "--commit"));
        var target = Assert.IsType<Option<string>>(command.Options.Single(option => option.Name == "--target"));
        Assert.Equal("artifacts/security results", parsed.GetValue(output));
        Assert.Equal("deadbeef", parsed.GetValue(commit));
        Assert.Equal("http://app:8080/", parsed.GetValue(target));
    }

    [Fact]
    public void SecurityScanOutputMetadataRemainsOptional()
    {
        var parsed = Program.Parse(Program.BuildRoot(),
            ["security", "scan", "scanner", "--tool", "zap", "--target", "http://app/"]);
        Assert.Empty(parsed.Errors);
        var options = parsed.CommandResult.Command.Options;
        Assert.Null(parsed.GetValue(Assert.IsType<Option<string?>>(options.Single(option => option.Name == "--output"))));
        Assert.Null(parsed.GetValue(Assert.IsType<Option<string?>>(options.Single(option => option.Name == "--commit"))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void IncompleteResultsUseReservedExitStatusAndPreserveRawOutput(int scannerExitCode)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new ScanRunMetadata("run", now, now, "zap", "image", "rules", "scanner", "isolated", true, "http://app/");
        var result = new ScanResult(run, "incomplete", IncompleteReason: "unperformed-checks-lost", ScannerExitCode: scannerExitCode);
        const string report = "{\"site\":[]}";
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", scannerExitCode, report)
        {
            Result = result
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(70, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, true));

        Assert.Equal(report, output.ToString());
        Assert.Contains($"exit={scannerExitCode}", error.ToString());
        Assert.Contains("scan result incomplete: unperformed-checks-lost", error.ToString());
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(70, 1)]
    public void CompletedResultsPreserveScannerMetadataAndReserveTheIncompleteCode(int scannerExitCode, int processExitCode)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new ScanRunMetadata("run", now, now, "zap", "image", "rules", "scanner", "isolated", true, "http://app/");
        var result = new ScanResult(run, "completed", Findings: [], UnperformedChecks: "active scan omitted", ScannerExitCode: scannerExitCode);
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", scannerExitCode, "{\"site\":[]}")
        {
            Result = result
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(processExitCode, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, true));
        Assert.Equal(scannerExitCode, outcome.Result!.ScannerExitCode);
        Assert.Contains($"exit={scannerExitCode}", error.ToString());
        Assert.Equal(outcome.Report, output.ToString());
        Assert.DoesNotContain("scan result incomplete", error.ToString());
    }

    [Fact]
    public void PersistenceErrorsAreReportedWithoutReplacingRawOutputOrScannerStatus()
    {
        const string report = "{\"site\":[]}";
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", 7, report,
            PersistenceError: "disk full\u001b[2J");
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(7, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, true));

        Assert.Equal(report, output.ToString());
        Assert.Contains("exit=7", error.ToString());
        Assert.Contains("result persistence failed: disk full\\u001b[2J", error.ToString());
    }

    [Fact]
    public void CapturedStreamsExplicitlyUseUtf8()
    {
        var startInfo = new ProcessStartInfo();
        CommandRunner.ConfigureCapturedStreams(startInfo);
        Assert.Equal(Encoding.UTF8.CodePage, startInfo.StandardOutputEncoding?.CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, startInfo.StandardErrorEncoding?.CodePage);
    }

    [Fact]
    public void CapturedUtf8IgnoresTheConsoleCodePage()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The byte-producing fixture uses /bin/sh");
        var original = Console.OutputEncoding;
        try
        {
            Console.OutputEncoding = Encoding.ASCII;
            var output = new StringWriter();
            var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"), output, new StringWriter());
            Assert.Equal(0, runner.Run(["/bin/sh", "-c", "printf '\\343\\201\\202\\303\\251'"]));
            Assert.Equal("あé", output.ToString());
        }
        finally
        {
            Console.OutputEncoding = original;
        }
    }

    [Fact]
    public void TerminalReportsEscapeControlsAndBidiOverrides()
    {
        var output = new StringWriter();
        CliContext.WriteSecurityReport(output, "Title\n\u001b[2J\u001b]8;;fake\a\r\u0085\u009b\u009d\u202e", isOutputRedirected: false);
        Assert.Equal("Title\n\\u001b[2J\\u001b]8;;fake\\u0007\\u000d\\u0085\\u009b\\u009d\\u202e", output.ToString());
    }

    [Fact]
    public void RedirectedReportsPreserveTheOriginalText()
    {
        const string report = "あé\n\u001b[2J\r";
        var output = new StringWriter();
        CliContext.WriteSecurityReport(output, report, isOutputRedirected: true);
        Assert.Equal(report, output.ToString());
    }

    [Fact]
    public void ARedirectedReportReachesStdoutAsUtf8WithoutABom()
    {
        const string report = "{\"title\":\"あéĀ\"}";
        using var stdout = new MemoryStream();
        var output = CliContext.ReportOutput(isOutputRedirected: true, () => stdout, TextWriter.Null);

        var encoding = Assert.IsType<UTF8Encoding>(output.Encoding);
        Assert.Empty(encoding.GetPreamble());
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", 0, report);
        CliContext.WriteScanOutcome(outcome, "/report.json", output, TextWriter.Null, isOutputRedirected: true);

        Assert.Equal(Encoding.UTF8.GetBytes(report), stdout.ToArray());
    }

    [Fact]
    public void ATerminalReportKeepsTheConsoleWriter()
    {
        using var console = new StringWriter();
        Assert.Same(console, CliContext.ReportOutput(isOutputRedirected: false,
            () => throw new InvalidOperationException("stdout must not be reopened for a terminal"), console));
    }

    [Fact]
    public void TerminalReportsUseOneWriteInsteadOfOneWritePerCharacter()
    {
        var output = new CountingWriter();
        CliContext.WriteSecurityReport(output, new string('x', 1024 * 1024) + "\u001b", isOutputRedirected: false);
        Assert.Equal(1, output.Writes);
        Assert.EndsWith("\\u001b", output.ToString());
    }

    [Fact]
    public void ExcessiveTimeoutIsRejectedBeforePreparingAnImage()
    {
        var context = new CliContext(new CliOptions("missing-security-test-config.yml", null, false, null, false));
        var failure = Assert.Throws<ConfigException>(() => context.SecurityScan("unused", "unused", "unused", 99_999_999));
        Assert.Contains("--timeout", failure.Message);
        CliContext.ValidateSecurityTimeout(2_147_483);
    }

    [Fact]
    public void ToolExit124IsNotAnEnforcedTimeout()
    {
        var command = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "cmd.exe"), "/c", "exit /b 124" }
            : new[] { "/bin/sh", "-c", "exit 124" };
        var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"), TextWriter.Null, TextWriter.Null);
        var result = runner.RunWithOutcome(command, timeout: TimeSpan.FromSeconds(5));
        Assert.Equal(124, result.Code);
        Assert.False(result.TimedOut);
        Assert.Equal(124, runner.Run(command, timeout: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ExistingHealthProbeAcceptsLargeStderrAndPreservesTheLastLine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The large-output fixture uses /bin/sh");
        var context = new CliContext(new CliOptions(null, null, false, null, false));
        var result = context.Probe(
            ["/bin/sh", "-c", "head -c 1048577 /dev/zero | tr '\\000' x >&2; printf '\\nhealth-tail' >&2; exit 17"],
            timeout: TimeSpan.FromSeconds(5), captureStderr: true);
        Assert.Equal(17, result.Code);
        Assert.Equal(1_048_589, result.Output.Length);
        Assert.EndsWith("health-tail", CliContext.HealthCheckFailureMessage("db", 1, result.Code, result.Output));
    }

    [Fact]
    public void ReportReadFailurePreservesProvenanceAndTheScannerExitCode()
    {
        var outcome = new ScanOutcome("scanner", "scan", "image", "rules-v1", "isolated", 7, null,
            ReportError: "report exceeded the output limit");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(7, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, isOutputRedirected: true));
        Assert.Contains("scanner\tscan\tisolated\timage\trules=rules-v1\texit=7\tstatus=exited", error.ToString());
        Assert.Contains("report exceeded the output limit", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void EnforcedDeadlineIsReportedOutOfBand()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The blocking fixture uses /bin/sh");
        var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"), TextWriter.Null, TextWriter.Null);
        var command = new[] { "/bin/sh", "-c", "exec sleep 5" };
        var result = runner.RunWithOutcome(command, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Equal(CommandRunner.TimeoutExitCode, result.Code);
        Assert.True(result.TimedOut);
        Assert.Equal(CommandRunner.TimeoutExitCode, runner.Run(command, timeout: TimeSpan.FromMilliseconds(100)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundedReportsPreserveStatusAndDiscardOverflowWithoutThrowing(bool timedOut)
    {
        var result = CliContext.CaptureProbeWithOutcome((output, _) =>
        {
            output.Write(new string('x', ScanEngine.MaxReportCharacters + 1));
            return (71, timedOut);
        }, boundedReport: true);
        Assert.Equal(71, result.Code);
        Assert.Equal(timedOut, result.TimedOut);
        Assert.True(result.OutputLimitExceeded);
        Assert.Empty(result.Output);
    }

    [Fact]
    public void EmptyAlertsDoNotHideRawUnperformedChecks()
    {
        const string diagnostics = "Checks not performed: authentication\nNative warning: spider limit reached\n";
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", 2, "{\"alerts\":[]}",
            UnperformedChecks: diagnostics);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, true));

        Assert.Equal(outcome.Report, output.ToString());
        Assert.Contains("Unperformed checks (raw):", error.ToString());
        Assert.Contains(diagnostics, error.ToString());
    }

    [Fact]
    public void MissingUnperformedCheckDiagnosticsAreExplicit()
    {
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", 0, "{\"alerts\":[]}",
            UnperformedChecksError: "collection timed out");
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, true));

        Assert.Contains("unperformed-check diagnostics unavailable: collection timed out", error.ToString());
    }

    /// <summary>
    /// An empty sidecar used to print its header with nothing under it, which reads as "no
    /// checks were skipped" -- the one reading this collection exists to prevent. The scanner
    /// asked for the file, so an empty one means the record was lost. Whitespace is the same
    /// case: a header followed by a blank line reads as "none" just as a bare header does.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("  \r\n\t ")]
    public void AnEmptyUnperformedCheckRecordIsReportedRatherThanShownAsNone(string sidecar)
    {
        var outcome = new ScanOutcome("zap", "scanner", "image", "rules", "isolated", 0, "{\"alerts\":[]}",
            UnperformedChecks: sidecar);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CliContext.WriteScanOutcome(outcome, "/report.json", output, error, true));

        var written = error.ToString();
        Assert.DoesNotContain("Unperformed checks (raw):", written);
        Assert.Contains("came back empty", written);
    }

    private sealed class CountingWriter : StringWriter
    {
        internal int Writes { get; private set; }
        public override void Write(char value) { Writes++; base.Write(value); }
        public override void Write(string? value) { Writes++; base.Write(value); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneralProbePreservesLargeOutputAndTheExitCode(bool captureStderr)
    {
        var text = new string('x', 1_048_577);
        var result = CliContext.CaptureProbeWithOutcome((output, error) =>
        {
            (captureStderr ? error : output).Write(text);
            return (17, false);
        }, captureStderr);
        Assert.Equal(17, result.Code);
        Assert.Equal(text, result.Output);
    }

    private static (ParseResult Parsed, ZapProfile? Profile) ParseZap(params string[] options)
    {
        var parsed = Program.Parse(Program.BuildRoot(),
            ["security", "scan", "scanner", "--tool", "zap", "--target", "http://app/", .. options]);
        Assert.Empty(parsed.Errors);
        var all = parsed.CommandResult.Command.Options;
        Option<int> Bound(string name) => Assert.IsType<Option<int>>(all.Single(option => option.Name == name));
        var profile = Program.ZapProfileFrom(parsed, Bound(ZapProfile.SpiderMinutesOption), Bound(ZapProfile.SpiderDepthOption),
            Bound(ZapProfile.SpiderChildrenOption), Bound(ZapProfile.PassiveWaitMinutesOption),
            Assert.IsType<Option<bool>>(all.Single(option => option.Name == ZapProfile.ActiveScanOption)),
            Bound(ZapProfile.ActiveScanMinutesOption));
        return (parsed, profile);
    }

    [Fact]
    public void NoZapOptionsKeepTheDefaultScanner()
    {
        Assert.Null(ParseZap().Profile);
        Assert.Same(ScanEngine.Select("zap"), CliContext.SelectScanner("zap", null, 1800));
    }

    [Fact]
    public void ZapOptionsBuildTheCallersProfile()
    {
        Assert.Equal(new ZapProfile(SpiderMinutes: 10, SpiderDepth: 4), ParseZap("--zap-spider-minutes", "10", "--zap-spider-depth", "4").Profile);
        Assert.Equal(new ZapProfile(ActiveScan: true), ParseZap("--zap-active-scan").Profile);
        Assert.Equal(new ZapProfile(ActiveScan: true, ActiveScanMinutes: 20),
            ParseZap("--zap-active-scan", "--zap-active-scan-minutes", "20").Profile);
    }

    [Fact]
    public void AnActiveScanDurationWithoutTheActiveScanIsRefused()
    {
        var error = Assert.Throws<ConfigException>(() => ParseZap("--zap-active-scan-minutes", "20"));
        Assert.Contains("--zap-active-scan-minutes requires --zap-active-scan", error.Message);
    }

    [Fact]
    public void ZapBoundsAreRefusedForAnotherTool()
    {
        var error = Assert.Throws<ConfigException>(() => CliContext.SelectScanner("nuclei", new ZapProfile(SpiderDepth: 3), 1800));
        Assert.Contains("apply only to --tool zap", error.Message);
    }

    [Fact]
    public void ZapBoundsThatCannotFitTheDeadlineAreRefused()
    {
        var profile = new ZapProfile(SpiderMinutes: 20, ActiveScan: true, ActiveScanMinutes: 15);
        var error = Assert.Throws<ConfigException>(() => CliContext.SelectScanner("zap", profile, 1800));
        Assert.Contains("raise --timeout", error.Message);
        Assert.Contains("start-up and report generation", error.Message);

        var scanner = Assert.IsType<ZapScanner>(CliContext.SelectScanner("zap", profile, 3600));
        Assert.Equal(profile, scanner.Profile);
    }

    [Fact]
    public void TheDefaultZapBoundsMustFitTheDeadlineToo()
    {
        // Spider 1 min + passive wait 1 min = 120 s, as in the plan no option changes.
        var error = Assert.Throws<ConfigException>(() => CliContext.SelectScanner("zap", null, 60));
        Assert.Contains("does not exceed the 2 minutes", error.Message);

        Assert.Same(ScanEngine.Select("zap"), CliContext.SelectScanner("zap", null, 121));
        Assert.Same(ScanEngine.Select("nuclei"), CliContext.SelectScanner("nuclei", null, 60));
    }

    [Fact]
    public void ZapBoundsAreValidatedBeforeTheDeadlineIsCompared()
    {
        var error = Assert.Throws<ConfigException>(() => CliContext.SelectScanner("zap", new ZapProfile(SpiderMinutes: 0), 1800));
        Assert.Contains("--zap-spider-minutes must be between 1 and 30", error.Message);
    }

    private static ZapProfile? ParseZapLogin(params string[] options) => ParseZapLoginFor("zap", options);

    private static ZapProfile? ParseZapLoginFor(string tool, params string[] options)
    {
        var parsed = Program.Parse(Program.BuildRoot(),
            ["security", "scan", "scanner", "--tool", tool, "--target", "http://app/", .. options]);
        Assert.Empty(parsed.Errors);
        var all = parsed.CommandResult.Command.Options;
        Option<int> Bound(string name) => Assert.IsType<Option<int>>(all.Single(option => option.Name == name));
        Option<string?> Text(string name) => Assert.IsType<Option<string?>>(all.Single(option => option.Name == name));
        return Program.ZapProfileFrom(parsed, Bound(ZapProfile.SpiderMinutesOption), Bound(ZapProfile.SpiderDepthOption),
            Bound(ZapProfile.SpiderChildrenOption), Bound(ZapProfile.PassiveWaitMinutesOption),
            Assert.IsType<Option<bool>>(all.Single(option => option.Name == ZapProfile.ActiveScanOption)),
            Bound(ZapProfile.ActiveScanMinutesOption),
            new Program.ZapLoginOptions(Text(ZapLogin.LoginUrlOption), Text(ZapLogin.LoginBodyOption),
                Text(ZapLogin.UsernameOption), Text(ZapLogin.PasswordOption),
                Text(ZapLogin.LoggedInRegexOption), Text(ZapLogin.LoggedOutRegexOption)), tool);
    }

    private static readonly string[] LoginArgs =
    [
        "--zap-login-url", "http://app/login", "--zap-login-body", "user={%username%}&pass={%password%}",
        "--zap-login-username", "tester", "--zap-login-password", "pw", "--zap-logged-in-regex", "Sign out",
    ];

    [Fact]
    public void NoZapLoginOptionsKeepTheDefaultScanner()
    {
        Assert.Null(ParseZapLogin());
        Assert.Equal(new ZapProfile(ActiveScan: true), ParseZapLogin("--zap-active-scan"));
    }

    [Fact]
    public void ZapLoginOptionsBuildTheCallersLogin()
    {
        Assert.Equal(new ZapProfile(Login: new ZapLogin("http://app/login", "user={%username%}&pass={%password%}",
            "tester", "pw", LoggedInRegex: "Sign out")), ParseZapLogin(LoginArgs));

        var profile = ParseZapLogin([.. LoginArgs.Take(6), "--zap-logged-out-regex", "Sign in", "--zap-active-scan"]);
        Assert.Equal(new ZapProfile(ActiveScan: true, Login: new ZapLogin("http://app/login",
            "user={%username%}&pass={%password%}", "tester", "", LoggedOutRegex: "Sign in")), profile);
    }

    [Theory]
    [InlineData("--zap-login-url")]
    [InlineData("--zap-login-body")]
    [InlineData("--zap-login-username")]
    public void APartialZapLoginIsRefusedNamingTheMissingFlag(string missing)
    {
        var options = new List<string>();
        for (var i = 0; i < LoginArgs.Length; i += 2)
            if (LoginArgs[i] != missing) options.AddRange([LoginArgs[i], LoginArgs[i + 1]]);

        var error = Assert.Throws<ConfigException>(() => ParseZapLogin([.. options]));
        Assert.Contains($"{missing} is required", error.Message);
    }

    [Fact]
    public void AZapLoginWithoutAnIndicatorIsRefused()
    {
        var error = Assert.Throws<ConfigException>(() => ParseZapLogin([.. LoginArgs.Take(8)]));
        Assert.Contains("--zap-logged-in-regex or --zap-logged-out-regex is required", error.Message);

        var single = Assert.Throws<ConfigException>(() => ParseZapLogin("--zap-login-password", "pw"));
        Assert.Contains("--zap-login-url is required", single.Message);
    }

    [Fact]
    public void AZapLoginIndicatorWithAControlCharacterIsRefused()
    {
        var error = Assert.Throws<ConfigException>(() => ParseZapLogin([.. LoginArgs.Take(8), "--zap-logged-out-regex", "Sign\nin"]));
        Assert.Contains("--zap-logged-out-regex must not contain control characters", error.Message);
    }

    [Fact]
    public void AZapLoginIndicatorIsNotCompiledByWip()
    {
        Assert.Equal(@"\Qa.b\E", ParseZapLogin([.. LoginArgs.Take(8), "--zap-logged-out-regex", @"\Qa.b\E"])!.Login!.LoggedOutRegex);
    }

    [Fact]
    public void ZapLoginOptionsAreRefusedForAnotherTool()
    {
        var error = Assert.Throws<ConfigException>(() => CliContext.SelectScanner("nuclei", ParseZapLogin(LoginArgs), 1800));
        Assert.Contains("apply only to --tool zap", error.Message);
    }

    [Theory]
    [InlineData("--zap-login-password", "pw")]
    [InlineData("--zap-login-url", "http://app/login")]
    [InlineData("--zap-logged-in-regex", "(")]
    [InlineData("--zap-active-scan-minutes", "20")]
    public void ZapOptionsForAnotherToolAreRefusedBeforeAnyZapSpecificCheck(string option, string value)
    {
        var error = Assert.Throws<ConfigException>(() => ParseZapLoginFor("nuclei", option, value));
        Assert.Equal("--zap-* options apply only to --tool zap, not --tool nuclei", error.Message);
        Assert.Null(ParseZapLoginFor("nuclei"));
    }

    [Fact]
    public void ZapLoginOptionsAreListedInTheScanHelp()
    {
        var scan = Program.Parse(Program.BuildRoot(), ["security", "scan", "scanner", "--tool", "zap", "--target", "http://app/"])
            .CommandResult.Command;
        foreach (var name in new[] { ZapLogin.LoginUrlOption, ZapLogin.LoginBodyOption, ZapLogin.UsernameOption,
                     ZapLogin.PasswordOption, ZapLogin.LoggedInRegexOption, ZapLogin.LoggedOutRegexOption })
        {
            var description = scan.Options.Single(option => option.Name == name).Description!;
            Assert.StartsWith("ZAP only:", description);
            Assert.Contains("a login needs --zap-login-url, --zap-login-body, --zap-login-username and " +
                "--zap-logged-in-regex or --zap-logged-out-regex; --zap-login-password is optional", description);
            Assert.DoesNotContain("with the other", description);
        }
    }
}
