using System.Diagnostics;
using System.Text.Json;

namespace Wip.Tests;

/// <summary>
/// Runs <c>scripts/herdr-report</c> under <c>/bin/sh</c>, as it runs inside the sandbox,
/// against real FIFOs made with <c>mkfifo</c>.
/// </summary>
public sealed class HerdrReportShimTests : IDisposable
{
    private static readonly string ScriptPath = Path.Combine(
        Path.GetDirectoryName(GoldenCorpus.Root)!, "..", "scripts", "herdr-report");

    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("herdr-report-");

    public void Dispose() => _work.Delete(recursive: true);

    private string Fifo
    {
        get
        {
            var path = Path.Combine(_work.FullName, "report.fifo");
            if (!File.Exists(path)) Assert.Equal(0, Run("/bin/sh", null, "-c", $"mkfifo -m 600 '{path}'").Code);
            return path;
        }
    }

    private Dictionary<string, string> FifoEnvironment => new() { ["WIP_REPORT_FIFO"] = Fifo };

    private static (int Code, string Stdout, string Stderr, TimeSpan Elapsed) Shim(
        IReadOnlyDictionary<string, string>? environment, params string[] arguments) =>
        Run("/bin/sh", environment, [ScriptPath, .. arguments]);

    private static (int Code, string Stdout, string Stderr, TimeSpan Elapsed) Run(
        string executable, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) start.Environment[key] = value;

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{executable} did not finish");
        }
        return (process.ExitCode, stdout.Result, stderr.Result, clock.Elapsed);
    }

    /// <summary>Reads the FIFO until its writer closes it, as the relay's <c>cat</c> does.</summary>
    private Task<string> ReadFifoOnce()
    {
        var path = Fifo;
        return Task.Run(() => File.ReadAllText(path), TestContext.Current.CancellationToken);
    }

    private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "the shim runs under a Linux /bin/sh");

    /// <summary>The script names no host, endpoint or token: its only target is the FIFO variable.</summary>
    [Fact]
    public void ShippedScriptHasNoBuiltInTargetOrCredential()
    {
        var text = File.ReadAllText(ScriptPath);
        Assert.Contains("herdr_report_transport() {\n    printf '%s\\n' \"$2\" >\"$1\"\n}", text);
        foreach (var removed in new[] { "host.docker.internal", "localhost", "WIP_REPORT_ENDPOINT", "WIP_REPORT_TOKEN", "token", "pane_id\\\"" })
            Assert.DoesNotContain(removed, text);
        Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", text);
    }

    [Fact]
    public void EmptyEnvironmentSucceedsSilently()
    {
        SkipOnWindows();
        var result = Shim(null, "report-agent", "--agent", "claude-code", "--state", "blocked", "--seq", "3");
        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
    }

    /// <summary>A path that is not a FIFO is left alone: no file is created or written.</summary>
    [Fact]
    public void PathThatIsNotAFifoIsUnavailableAndUntouched()
    {
        SkipOnWindows();
        var plain = Path.Combine(_work.FullName, "plain");
        File.WriteAllText(plain, "");
        var missing = Path.Combine(_work.FullName, "missing");

        foreach (var path in new[] { plain, missing, _work.FullName })
        {
            var result = Shim(new Dictionary<string, string> { ["WIP_REPORT_FIFO"] = path }, "report-agent", "--agent", "a", "--state", "idle");
            Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
        }
        Assert.Equal("", File.ReadAllText(plain));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task WritesExactlyOneLineWithoutPaneSourceOrToken()
    {
        SkipOnWindows();
        var read = ReadFifoOnce();

        var result = Shim(FifoEnvironment, "report-agent", "--agent", "claude-code", "--state", "blocked", "--seq", "3", "--message", "needs \"approval\" \\ now");

        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
        var written = await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.EndsWith("\n", written);
        Assert.Single(written.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var request = JsonDocument.Parse(written);
        Assert.Equal(["id", "method", "params"], request.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("pane.report_agent", request.RootElement.GetProperty("method").GetString());
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal(["agent", "state", "seq", "message"], parameters.EnumerateObject().Select(p => p.Name));
        Assert.Equal("claude-code", parameters.GetProperty("agent").GetString());
        Assert.Equal("blocked", parameters.GetProperty("state").GetString());
        Assert.Equal(3, parameters.GetProperty("seq").GetInt64());
        Assert.Equal("needs \"approval\" \\ now", parameters.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OptionalFieldsAreOmittedWhenNotGiven()
    {
        SkipOnWindows();
        var read = ReadFifoOnce();
        Assert.Equal(0, Shim(FifoEnvironment, "report-agent", "--state", "idle", "--agent", "codex").Code);
        using var request = JsonDocument.Parse(await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(["agent", "state"], request.RootElement.GetProperty("params").EnumerateObject().Select(p => p.Name));
    }

    /// <summary>
    /// With no relay reading, opening the FIFO blocks; the deadline cuts it off at two
    /// seconds, silently, and leaves no blocked writer behind.
    /// </summary>
    [Fact]
    public void NoReaderIsCutOffAtTheDeadlineAndSucceedsSilently()
    {
        SkipOnWindows();
        var copy = Path.Combine(_work.FullName, "herdr-report-deadline-" + Guid.NewGuid().ToString("N")[..8]);
        File.Copy(ScriptPath, copy);

        var result = Run("/bin/sh", FifoEnvironment, copy, "report-agent", "--agent", "a", "--state", "working");

        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
        Assert.InRange(result.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4));
        Assert.DoesNotContain(Process.GetProcesses(), p => CommandLine(p).Contains(copy, StringComparison.Ordinal));
    }

    private static string CommandLine(Process process)
    {
        try
        {
            return File.ReadAllText($"/proc/{process.Id}/cmdline");
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    [Theory]
    [InlineData]
    [InlineData("report")]
    [InlineData("pane.report_agent")]
    [InlineData("report-agent")]
    [InlineData("report-agent", "--state", "idle")]
    [InlineData("report-agent", "--agent", "a")]
    [InlineData("report-agent", "--agent", "", "--state", "idle")]
    [InlineData("report-agent", "--agent", "a", "--state", "done")]
    [InlineData("report-agent", "--agent", "a", "--state", "IDLE")]
    [InlineData("report-agent", "--agent", "a", "--state")]
    [InlineData("report-agent", "--agent", "a", "--state", "idle", "--seq", "-1")]
    [InlineData("report-agent", "--agent", "a", "--state", "idle", "--seq", "03")]
    [InlineData("report-agent", "--agent", "a", "--state", "idle", "--seq", "1.5")]
    [InlineData("report-agent", "--agent", "a", "--state", "idle", "--seq", "1234567890123456")]
    [InlineData("report-agent", "--agent", "a", "--state", "idle", "--message", "")]
    [InlineData("report-agent", "--agent", "a\nb", "--state", "idle")]
    [InlineData("report-agent", "--agent", "a", "--state", "idle", "--message", "line\nbreak")]
    [InlineData("report-agent", "--agent", "a", "--agent", "b", "--state", "idle")]
    [InlineData("report-agent", "--agent", "a", "--state", "idle", "--session", "s")]
    [InlineData("report-agent", "--agent", "a", "--state", "idle", "extra")]
    [InlineData("report-agent-session", "--agent", "a")]
    [InlineData("report-agent-session", "--session", "s")]
    [InlineData("report-agent-session", "--agent", "a", "--session", "s", "--state", "idle")]
    public void InvalidArgumentsExitTwoBeforeWriting(params string[] arguments)
    {
        SkipOnWindows();
        // No reader: a write attempt would block until the deadline and then exit 0.
        var result = Shim(FifoEnvironment, arguments);
        Assert.Equal(2, result.Code);
        Assert.NotEmpty(result.Stderr);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(1.5), $"took {result.Elapsed}");
    }

    [Fact]
    public void ReportLongerThanAnAtomicFifoWriteIsRefused()
    {
        SkipOnWindows();
        var result = Shim(FifoEnvironment, "report-agent", "--agent", "a", "--state", "idle", "--message", new string('x', 4096));
        Assert.Equal(2, result.Code);
        Assert.Contains("longer than 4096 bytes", result.Stderr);
    }

    /// <summary>
    /// Nothing on the command line reaches the method, the target or the pane: such flags
    /// are unknown, and are refused before anything is written.
    /// </summary>
    [Theory]
    [InlineData("--method", "pane.send_input")]
    [InlineData("--fifo", "/tmp/other.fifo")]
    [InlineData("--endpoint", "evil.test:1")]
    [InlineData("--target", "evil.test:1")]
    [InlineData("--host", "evil.test")]
    [InlineData("--port", "1")]
    [InlineData("--token", "other")]
    [InlineData("--pane", "pane-1")]
    [InlineData("--pane-id", "pane-1")]
    [InlineData("--pane_id", "pane-1")]
    [InlineData("--source", "wip:other")]
    [InlineData("--raw", "{\"method\":\"pane.read\"}")]
    [InlineData("--params", "{}")]
    [InlineData("--agent=a", "x")]
    public void NoArgumentOverridesMethodOrTarget(string flag, string value)
    {
        SkipOnWindows();
        var result = Shim(FifoEnvironment, "report-agent", "--agent", "a", "--state", "idle", flag, value);
        Assert.Equal(2, result.Code);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(1.5), $"took {result.Elapsed}");
    }

    [Fact]
    public async Task InjectedJsonInValuesStaysLiteral()
    {
        SkipOnWindows();
        const string agent = "a\",\"method\":\"pane.send_input\",\"x\":\"";
        const string message = "\"},\"method\":\"pane.read\",\"params\":{\"pane_id\":\"pane-1";
        var read = ReadFifoOnce();

        Assert.Equal(0, Shim(FifoEnvironment, "report-agent", "--agent", agent, "--state", "idle", "--message", message).Code);

        using var request = JsonDocument.Parse(await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("pane.report_agent", request.RootElement.GetProperty("method").GetString());
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal(agent, parameters.GetProperty("agent").GetString());
        Assert.Equal(message, parameters.GetProperty("message").GetString());
        Assert.False(parameters.TryGetProperty("pane_id", out _));
    }

    /// <summary>
    /// Herdr requires pane_id for the session method (measured), but its remaining params
    /// are unenumerated, so valid arguments are refused and nothing is written.
    /// </summary>
    [Fact]
    public void ReportAgentSessionValidatesThenRefusesWithoutWriting()
    {
        SkipOnWindows();
        var result = Shim(FifoEnvironment, "report-agent-session", "--agent", "claude-code", "--session", "session-abc", "--seq", "4");
        Assert.Equal(2, result.Code);
        Assert.Contains("nothing was sent", result.Stderr);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(1.5), $"took {result.Elapsed}");
    }

    [Fact]
    public void HelpPrintsUsageAndSucceeds()
    {
        SkipOnWindows();
        var result = Shim(null, "--help");
        Assert.Equal(0, result.Code);
        Assert.Contains("report-agent --agent NAME --state", result.Stdout);
    }
}
