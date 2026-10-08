using System.Diagnostics;
using System.Text.Json;

namespace Wip.Tests;

/// <summary>
/// Runs <c>scripts/herdr-report</c> under <c>/bin/sh</c>, as it runs inside the sandbox.
/// </summary>
/// <remarks>
/// The shipped transport is a stub until the TCP leg passes its gate, and the script has no
/// runtime seam for replacing it. Tests that need a transport run a copy with
/// <c>herdr_report_transport</c> redefined just before the final <c>herdr_report_main</c>
/// call; a later shell function definition replaces the earlier one.
/// </remarks>
public sealed class HerdrReportShimTests : IDisposable
{
    private const string MainCall = "herdr_report_main \"$@\"";

    private static readonly string ScriptPath = Path.Combine(
        Path.GetDirectoryName(GoldenCorpus.Root)!, "..", "scripts", "herdr-report");

    private static readonly Dictionary<string, string> RelayEnvironment = new()
    {
        ["WIP_REPORT_ENDPOINT"] = "relay.test:40123",
        ["WIP_REPORT_SOURCE"] = "wip:first",
        ["WIP_REPORT_TOKEN"] = "secret-token",
        ["HERDR_PANE_ID"] = "pane-7",
    };

    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("herdr-report-");

    public void Dispose() => _work.Delete(recursive: true);

    private string Captured(string name) => Path.Combine(_work.FullName, name);

    private bool TransportCalled => File.Exists(Captured("frames"));

    /// <summary>A copy whose transport records its target and frames, then answers with <paramref name="reply"/>.</summary>
    private string RecordingScript(string reply)
    {
        File.WriteAllText(Captured("reply"), reply);
        return ScriptWithTransport($$"""
            herdr_report_transport() {
                printf '%s\n%s\n' "$1" "$2" > '{{Captured("target")}}'
                cat > '{{Captured("frames")}}'
                cat '{{Captured("reply")}}'
            }
            """);
    }

    private string ScriptWithTransport(string transport)
    {
        var original = File.ReadAllText(ScriptPath);
        var index = original.LastIndexOf(MainCall, StringComparison.Ordinal);
        var path = Captured("herdr-report-under-test");
        File.WriteAllText(path, original[..index] + transport + "\n" + original[index..]);
        return path;
    }

    private static (int Code, string Stdout, string Stderr, TimeSpan Elapsed) Run(
        string script, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add(script);
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
            throw new TimeoutException("herdr-report did not finish");
        }
        return (process.ExitCode, stdout.Result, stderr.Result, clock.Elapsed);
    }

    private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "the shim runs under a Linux /bin/sh");

    private (string Token, JsonElement Request) Frames()
    {
        var lines = File.ReadAllLines(Captured("frames"));
        Assert.Equal(2, lines.Length);
        using var token = JsonDocument.Parse(lines[0]);
        Assert.Equal(["token"], token.RootElement.EnumerateObject().Select(p => p.Name));
        using var request = JsonDocument.Parse(lines[1]);
        return (token.RootElement.GetProperty("token").GetString()!, request.RootElement.Clone());
    }

    /// <summary>
    /// The shipped transport sends nothing until the TCP gate passes, and the script names
    /// no host of its own to fall back to.
    /// </summary>
    [Fact]
    public void ShippedScriptHasAStubTransportAndNoBuiltInAddress()
    {
        var text = File.ReadAllText(ScriptPath);
        Assert.EndsWith(MainCall + "\n", text);
        Assert.Contains("herdr_report_transport() {\n    return 69\n}", text);
        Assert.DoesNotContain("host.docker.internal", text);
        Assert.DoesNotContain("127.0.0.1", text);
        Assert.DoesNotContain("localhost", text);
        Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", text);
    }

    [Fact]
    public void EmptyEnvironmentSucceedsSilently()
    {
        SkipOnWindows();
        var result = Run(ScriptPath, null, "report-agent", "--agent", "claude-code", "--state", "blocked", "--seq", "3");
        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
    }

    /// <summary>With the full relay environment the shipped stub reports unavailable, which is still silent success.</summary>
    [Fact]
    public void ShippedStubTransportSucceedsSilently()
    {
        SkipOnWindows();
        var result = Run(ScriptPath, RelayEnvironment, "report-agent", "--agent", "claude-code", "--state", "working");
        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
    }

    [Theory]
    [InlineData("WIP_REPORT_ENDPOINT")]
    [InlineData("WIP_REPORT_TOKEN")]
    [InlineData("HERDR_PANE_ID")]
    public void MissingRelayVariableSucceedsSilentlyWithoutSending(string missing)
    {
        SkipOnWindows();
        var environment = new Dictionary<string, string>(RelayEnvironment);
        environment.Remove(missing);
        var result = Run(RecordingScript("""{"result":{"type":"ok"}}"""), environment, "report-agent", "--agent", "a", "--state", "idle");
        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
        Assert.False(TransportCalled);
    }

    [Theory]
    [InlineData("relay.test")]
    [InlineData("relay.test:")]
    [InlineData(":40123")]
    [InlineData("relay.test:0")]
    [InlineData("relay.test:65536")]
    [InlineData("relay.test:http")]
    [InlineData("relay test:1")]
    public void MalformedEndpointIsTreatedAsUnavailable(string endpoint)
    {
        SkipOnWindows();
        var environment = new Dictionary<string, string>(RelayEnvironment) { ["WIP_REPORT_ENDPOINT"] = endpoint };
        var result = Run(RecordingScript("""{"result":{"type":"ok"}}"""), environment, "report-agent", "--agent", "a", "--state", "idle");
        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
        Assert.False(TransportCalled);
    }

    [Theory]
    [InlineData("relay.test:40123", "relay.test", "40123")]
    [InlineData("10.1.2.3:9", "10.1.2.3", "9")]
    [InlineData("[fd00::1]:65535", "fd00::1", "65535")]
    public void EndpointIsPassedToTheTransportAsGiven(string endpoint, string host, string port)
    {
        SkipOnWindows();
        var environment = new Dictionary<string, string>(RelayEnvironment) { ["WIP_REPORT_ENDPOINT"] = endpoint };
        Assert.Equal(0, Run(RecordingScript("""{"result":{"type":"ok"}}"""), environment, "report-agent", "--agent", "a", "--state", "idle").Code);
        Assert.Equal([host, port], File.ReadAllLines(Captured("target")));
    }

    /// <summary>The token line comes first, then exactly one request with the verified params.</summary>
    [Fact]
    public void SendsTokenLineThenOneRequestLine()
    {
        SkipOnWindows();
        var result = Run(RecordingScript("""{"id":"x","result":{"type":"ok"}}"""), RelayEnvironment,
            "report-agent", "--agent", "claude-code", "--state", "blocked", "--seq", "3", "--message", "needs \"approval\" \\ now");

        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
        var (token, request) = Frames();
        Assert.Equal("secret-token", token);
        Assert.Equal(["id", "method", "params"], request.EnumerateObject().Select(p => p.Name));
        Assert.Equal("pane.report_agent", request.GetProperty("method").GetString());
        var parameters = request.GetProperty("params");
        Assert.Equal(["pane_id", "agent", "state", "seq", "message"], parameters.EnumerateObject().Select(p => p.Name));
        Assert.Equal("pane-7", parameters.GetProperty("pane_id").GetString());
        Assert.Equal("claude-code", parameters.GetProperty("agent").GetString());
        Assert.Equal("blocked", parameters.GetProperty("state").GetString());
        Assert.Equal(3, parameters.GetProperty("seq").GetInt64());
        Assert.Equal("needs \"approval\" \\ now", parameters.GetProperty("message").GetString());
    }

    [Fact]
    public void OptionalFieldsAreOmittedWhenNotGiven()
    {
        SkipOnWindows();
        Assert.Equal(0, Run(RecordingScript("""{"result":{"type":"ok"}}"""), RelayEnvironment, "report-agent", "--state", "idle", "--agent", "codex").Code);
        Assert.Equal(["pane_id", "agent", "state"], Frames().Request.GetProperty("params").EnumerateObject().Select(p => p.Name));
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
    public void InvalidArgumentsExitTwoWithoutSending(params string[] arguments)
    {
        SkipOnWindows();
        var result = Run(RecordingScript("""{"result":{"type":"ok"}}"""), RelayEnvironment, arguments);
        Assert.Equal(2, result.Code);
        Assert.NotEmpty(result.Stderr);
        Assert.False(TransportCalled);
    }

    /// <summary>
    /// Nothing on the command line reaches the method, the target, the token or the pane:
    /// such flags are unknown, and injected JSON in a value stays a literal string.
    /// </summary>
    [Theory]
    [InlineData("--method", "pane.send_input")]
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
        var result = Run(RecordingScript("""{"result":{"type":"ok"}}"""), RelayEnvironment,
            "report-agent", "--agent", "a", "--state", "idle", flag, value);
        Assert.Equal(2, result.Code);
        Assert.False(TransportCalled);
    }

    [Fact]
    public void InjectedJsonInValuesStaysLiteral()
    {
        SkipOnWindows();
        const string agent = "a\",\"method\":\"pane.send_input\",\"x\":\"";
        const string message = "\"},\"method\":\"pane.read\",\"params\":{\"pane_id\":\"pane-1";
        Assert.Equal(0, Run(RecordingScript("""{"result":{"type":"ok"}}"""), RelayEnvironment,
            "report-agent", "--agent", agent, "--state", "idle", "--message", message).Code);

        var request = Frames().Request;
        Assert.Equal("pane.report_agent", request.GetProperty("method").GetString());
        Assert.Equal(agent, request.GetProperty("params").GetProperty("agent").GetString());
        Assert.Equal(message, request.GetProperty("params").GetProperty("message").GetString());
        Assert.Equal("pane-7", request.GetProperty("params").GetProperty("pane_id").GetString());
    }

    /// <summary>
    /// Herdr requires pane_id for the session method (measured), but its remaining params
    /// are unenumerated, so valid arguments are refused and nothing is sent.
    /// </summary>
    [Fact]
    public void ReportAgentSessionValidatesThenRefusesWithoutSending()
    {
        SkipOnWindows();
        var result = Run(RecordingScript("""{"result":{"type":"ok"}}"""), RelayEnvironment,
            "report-agent-session", "--agent", "claude-code", "--session", "session-abc", "--seq", "4");
        Assert.Equal(2, result.Code);
        Assert.Contains("nothing was sent", result.Stderr);
        Assert.False(TransportCalled);
    }

    [Theory]
    [InlineData("""{"id":"r1","error":{"code":"relay_refused","message":"Unsupported method"}}""")]
    [InlineData("""{"id":"","error":{"code":"invalid_request","message":"missing field `state`"}}""")]
    public void RefusalExitsOneAndSaysWhy(string reply)
    {
        SkipOnWindows();
        var result = Run(RecordingScript(reply), RelayEnvironment, "report-agent", "--agent", "a", "--state", "idle");
        Assert.Equal(1, result.Code);
        Assert.Contains(reply, result.Stderr);
        Assert.Equal("", result.Stdout);
    }

    /// <summary>A relay that closes without answering is unavailable, not a refusal.</summary>
    [Fact]
    public void NoReplyIsSilentSuccess()
    {
        SkipOnWindows();
        var result = Run(RecordingScript(""), RelayEnvironment, "report-agent", "--agent", "a", "--state", "idle");
        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
        Assert.True(TransportCalled);
    }

    /// <summary>A hook is never held longer than the two-second deadline.</summary>
    [Fact]
    public void HungTransportIsCutOffAtTheDeadlineAndSucceedsSilently()
    {
        SkipOnWindows();
        var script = ScriptWithTransport("herdr_report_transport() { cat >/dev/null; sleep 47; }");
        var result = Run(script, RelayEnvironment, "report-agent", "--agent", "a", "--state", "working");
        Assert.Equal((0, "", ""), (result.Code, result.Stdout, result.Stderr));
        Assert.InRange(result.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4));
        // The cut-off transport's children are killed too, not left running behind the hook.
        Assert.DoesNotContain(Process.GetProcessesByName("sleep"), p => CommandLine(p) == "sleep\047\0");
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
    }

    [Fact]
    public void HelpPrintsUsageAndSucceeds()
    {
        SkipOnWindows();
        var result = Run(ScriptPath, null, "--help");
        Assert.Equal(0, result.Code);
        Assert.Contains("report-agent --agent NAME --state", result.Stdout);
    }
}
