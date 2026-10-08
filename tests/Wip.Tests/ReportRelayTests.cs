using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Wip.Execution;

namespace Wip.Tests;

public class ReportRelayTests
{
    private const string Source = "wip:first";
    private const string Pane = "pane-7";
    private const string Ok = """{"id":"b","result":{"type":"ok"}}""";

    private static readonly string ShimPath = Path.Combine(
        Path.GetDirectoryName(GoldenCorpus.Root)!, "..", "scripts", "herdr-report");

    private sealed class Recorder
    {
        public readonly List<string> Forwarded = [];
        public readonly List<string> Logged = [];
        public string Reply = Ok;

        public ReportRelayServer Server(ReportUpstream? upstream = null) => new("first", Pane, upstream ?? ((line, _) =>
        {
            lock (Forwarded) Forwarded.Add(line);
            return Task.FromResult(Reply);
        }), message => { lock (Logged) Logged.Add(message); });
    }

    [Theory]
    [InlineData("pane.report_agent")]
    [InlineData("pane.report_agent_session")]
    public void AllowedMethodsAreForwardedWithTheRelayPaneAndSource(string method)
    {
        var decision = ReportRelay.Filter($$$"""
            {"id":"r1","method":"{{{method}}}","params":{"agent":"claude","state":"working","seq":3,"agent_session_id":"s-1"}}
            """, Source, Pane);

        Assert.True(decision.Accepted);
        using var forwarded = JsonDocument.Parse(decision.Forward!);
        Assert.Equal("r1", forwarded.RootElement.GetProperty("id").GetString());
        Assert.Equal(method, forwarded.RootElement.GetProperty("method").GetString());
        var parameters = forwarded.RootElement.GetProperty("params");
        Assert.Equal(["agent", "state", "seq", "agent_session_id", "pane_id", "source"], parameters.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Pane, parameters.GetProperty("pane_id").GetString());
        Assert.Equal(Source, parameters.GetProperty("source").GetString());
        Assert.Equal(3, parameters.GetProperty("seq").GetInt32());
    }

    [Fact]
    public void MissingParamsStillGetPaneAndSource()
    {
        var decision = ReportRelay.Filter("""{"method":"pane.report_agent"}""", Source, Pane);
        Assert.Equal("""{"method":"pane.report_agent","params":{"pane_id":"pane-7","source":"wip:first"}}""", decision.Forward);
    }

    /// <summary>A sandbox cannot report under another sandbox's name.</summary>
    [Fact]
    public void SpoofedSourceIsReplaced()
    {
        var decision = ReportRelay.Filter("""{"method":"pane.report_agent","params":{"source":"wip:other"}}""", Source, Pane);

        Assert.True(decision.Accepted);
        using var forwarded = JsonDocument.Parse(decision.Forward!);
        Assert.Equal(Source, forwarded.RootElement.GetProperty("params").GetProperty("source").GetString());
    }

    /// <summary>
    /// The pane is the relay's to say. A line naming any pane -- another one, the relay's
    /// own, or a non-string -- is refused rather than rewritten.
    /// </summary>
    [Theory]
    [InlineData("""{"id":"r1","method":"pane.report_agent","params":{"pane_id":"pane-1","agent":"a","state":"idle"}}""")]
    [InlineData("""{"id":"r1","method":"pane.report_agent","params":{"pane_id":"pane-7","agent":"a","state":"idle"}}""")]
    [InlineData("""{"id":"r1","method":"pane.report_agent","params":{"pane_id":null}}""")]
    [InlineData("""{"id":"r1","method":"pane.report_agent","params":{"pane_id":7}}""")]
    [InlineData("""{"id":"r1","method":"pane.report_agent_session","params":{"pane_id":"pane-1","agent":"a"}}""")]
    public void SandboxSuppliedPaneIdIsRefused(string line)
    {
        var decision = ReportRelay.Filter(line, Source, Pane);

        Assert.False(decision.Accepted);
        Assert.Null(decision.Forward);
        Assert.Equal("r1", decision.Id);
        Assert.Contains("pane_id", decision.Error);
    }

    [Theory]
    [InlineData("""{"id":"r1","method":"pane.send_input","params":{"text":"rm -rf /"}}""")]
    [InlineData("""{"id":"r1","method":"pane.read","params":{}}""")]
    [InlineData("""{"id":"r1","method":"ping"}""")]
    [InlineData("""{"id":"r1","method":"PANE.REPORT_AGENT","params":{}}""")]
    [InlineData("""{"id":"r1","method":"pane.report_agent ","params":{}}""")]
    [InlineData("""{"id":"r1","method":"report-agent","params":{}}""")]
    [InlineData("""{"id":"r1","method":"report-agent-session","params":{}}""")]
    [InlineData("""{"id":"r1","method":"pane.report-agent","params":{}}""")]
    [InlineData("""{"id":"r1","method":"report_agent","params":{}}""")]
    [InlineData("""{"id":"r1","params":{}}""")]
    [InlineData("""{"id":"r1","method":["pane.report_agent"]}""")]
    [InlineData("""{"id":"r1","method":"pane.report_agent","params":[]}""")]
    [InlineData("""{"id":"r1","method":"pane.report_agent","subscribe":true}""")]
    [InlineData("""{"id":"r1","method":"pane.report_agent","token":"t"}""")]
    [InlineData("""{"id":7,"method":"pane.report_agent"}""")]
    [InlineData("""[{"method":"pane.report_agent"}]""")]
    [InlineData("""{"method":"pane.report_agent"}{"method":"pane.read"}""")]
    [InlineData("""{"token":"secret"}""")]
    [InlineData("not json")]
    public void EverythingElseIsRefused(string line)
    {
        var decision = ReportRelay.Filter(line, Source, Pane);

        Assert.False(decision.Accepted);
        Assert.Null(decision.Forward);
        Assert.NotNull(decision.Error);
    }

    [Fact]
    public void OversizedMessageIsRefused()
    {
        var padding = new string('x', ReportRelay.MaxLineBytes);
        Assert.False(ReportRelay.Filter($$$"""{"method":"pane.report_agent","params":{"note":"{{{padding}}}"}}""", Source, Pane).Accepted);
    }

    [Fact]
    public void ExecEnvironmentExportsFifoSourceAndPresentPassThroughOnly()
    {
        var host = new Dictionary<string, string> { ["HERDR_PANE_ID"] = "1-2", ["HERDR_SOCKET_PATH"] = "/host/herdr.sock" };

        var environment = ReportRelay.ExecEnvironment("first", name => host.GetValueOrDefault(name));

        Assert.Equal(
            [
                new("WIP_REPORT_FIFO", "/run/wip/report.fifo"),
                new("WIP_REPORT_SOURCE", Source),
                new("HERDR_PANE_ID", "1-2"),
            ],
            environment);
        Assert.Equal(2, ReportRelay.ExecEnvironment("first", _ => null).Count);
        Assert.Equal(2, ReportRelay.ExecEnvironment("first", _ => "1-2\n-e X=1").Count);
    }

    /// <summary>The FIFO helper text is fixed: the read command and the create script name only the fixed path.</summary>
    [Fact]
    public void FifoCommandsUseOnlyTheFixedPath()
    {
        Assert.Equal(["sh", "-c", "[ -p /run/wip/report.fifo ] && [ ! -L /run/wip/report.fifo ] && exec cat /run/wip/report.fifo"],
            ReportRelay.ReadFifoCommand);
        Assert.Contains("mkfifo -m 600 /run/wip/report.fifo", ReportRelay.CreateFifoScript);
        Assert.DoesNotContain("@", ReportRelay.CreateFifoScript);
    }

    /// <summary>The create script with the fixed directory swapped for <paramref name="directory"/>.</summary>
    private static string CreateFifoScriptIn(string directory) =>
        ReportRelay.CreateFifoScript.Replace(ReportRelay.FifoDirectory, directory, StringComparison.Ordinal);

    /// <summary>
    /// The create script under the sandbox's sh: a FIFO owned by the exec user, mode 0600 in a
    /// 0700 directory, and running it again reuses that FIFO.
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void CreateFifoScriptMakesAnOwnerOnlyFifoIdempotently()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the script runs under a Linux /bin/sh");
        var parent = Directory.CreateTempSubdirectory("wip-fifo-");
        try
        {
            var directory = Path.Combine(parent.FullName, "wip");
            var fifo = Path.Combine(directory, "report.fifo");

            Assert.Equal(0, Sh(CreateFifoScriptIn(directory)));
            Assert.Equal(0, Sh(CreateFifoScriptIn(directory)));

            Assert.Equal(0, Sh($"[ -p '{fifo}' ] && [ \"$(stat -c %u '{fifo}')\" = \"$(id -u)\" ]"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fifo));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    /// <summary>
    /// What sits at the FIFO path is reused only as created: anything else is refused and
    /// left exactly as found, never re-moded or replaced. (The script may still tighten its
    /// own directory to 0700; only the directory's type and owner are compared.)
    /// </summary>
    [Theory]
    [InlineData("regular file", "printf keep > \"$f\"")]
    [InlineData("symlink to a FIFO", "mkfifo -m 600 \"$d/../elsewhere\" && ln -s \"$d/../elsewhere\" \"$f\"")]
    [InlineData("FIFO with mode 0644", "mkfifo -m 644 \"$f\"")]
    [InlineData("FIFO with mode 0666", "mkfifo -m 600 \"$f\" && chmod 666 \"$f\"")]
    [InlineData("FIFO owned by another user", "mkfifo -m 600 \"$f\" && chown 4242 \"$f\"")]
    [InlineData("directory owned by another user", "chown 4242 \"$d\"")]
    [InlineData("directory that is a symlink", "rmdir \"$d\" && mkdir \"$d.real\" && ln -s \"$d.real\" \"$d\"")]
    public void CreateFifoScriptRefusesAnythingButItsOwnFifo(string what, string setup)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the script runs under a Linux /bin/sh");
        Assert.SkipWhen(setup.Contains("chown", StringComparison.Ordinal) && !IsRoot(), "changing a file's owner needs root");
        var parent = Directory.CreateTempSubdirectory("wip-fifo-");
        try
        {
            var directory = Path.Combine(parent.FullName, "wip");
            var fifo = Path.Combine(directory, "report.fifo");
            Directory.CreateDirectory(directory);
            Assert.Equal(0, Sh($"d='{directory}'; f='{fifo}'; {setup}"));
            var before = Describe(directory, fifo);

            Assert.NotEqual(0, Sh(CreateFifoScriptIn(directory)));

            Assert.Equal(before, Describe(directory, fifo));
            Assert.True(before.Length > 0, what);
        }
        finally
        {
            Sh($"chown -R \"$(id -u)\" '{parent.FullName}' 2>/dev/null");
            parent.Delete(recursive: true);
        }
    }

    private static bool IsRoot() => Sh("[ \"$(id -u)\" = 0 ]") == 0;

    private static string Describe(string directory, string fifo)
    {
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"stat -c '%F %u' '{directory}'; stat -c '%F %u %a' '{fifo}' 2>&1; [ -L '{fifo}' ] && echo symlink; [ -f '{fifo}' ] && cat '{fifo}'");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static int Sh(string script)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script }, RedirectStandardError = true })!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    [Fact]
    public async Task PumpSplitsLinesAndKeepsAnUnterminatedLast()
    {
        var lines = new List<string>();
        await ReportRelay.PumpLinesAsync(new StringReader("a\r\nb\n\nc"), line => { lines.Add(line); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b", "", "c"], lines);
    }

    /// <summary>An oversized line is passed on cut short, for the filter to refuse, and the rest is skipped.</summary>
    [Fact]
    public async Task PumpCutsAnOversizedLineAndRecoversAtTheNextNewline()
    {
        var lines = new List<string>();
        var input = new string('x', ReportRelay.MaxLineBytes * 3) + "\nnext\n";
        await ReportRelay.PumpLinesAsync(new StringReader(input), line => { lines.Add(line); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
        Assert.Equal(2, lines.Count);
        Assert.Equal(ReportRelay.MaxLineBytes + 1, lines[0].Length);
        Assert.False(ReportRelay.Filter(lines[0], Source, Pane).Accepted);
        Assert.Equal("next", lines[1]);
    }

    [Fact]
    public async Task HandleLineForwardsAcceptedLinesOnly()
    {
        var recorder = new Recorder();
        var server = recorder.Server();
        var cancellation = TestContext.Current.CancellationToken;

        await server.HandleLineAsync("""{"id":"a","method":"pane.read"}""", cancellation);
        await server.HandleLineAsync("""{"id":"p","method":"pane.report_agent","params":{"pane_id":"pane-1"}}""", cancellation);
        await server.HandleLineAsync("   ", cancellation);
        await server.HandleLineAsync("""{"id":"b","method":"pane.report_agent","params":{"agent":"a","state":"idle"}}""", cancellation);

        Assert.Equal(["""{"id":"b","method":"pane.report_agent","params":{"agent":"a","state":"idle","pane_id":"pane-7","source":"wip:first"}}"""],
            recorder.Forwarded);
        Assert.Equal(2, recorder.Logged.Count);
        Assert.Contains("refused a:", recorder.Logged[0]);
        Assert.Contains("refused p: pane_id", recorder.Logged[1]);
    }

    /// <summary>The sandbox gets no reply over a FIFO, so Herdr's rejection is logged on the host.</summary>
    [Fact]
    public async Task HerdrRejectionIsLoggedUnchanged()
    {
        const string herdrError = """{"id":"","error":{"code":"invalid_request","message":"missing field `state`"}}""";
        var recorder = new Recorder { Reply = herdrError };

        await recorder.Server().HandleLineAsync("""{"id":"b","method":"pane.report_agent","params":{}}""", TestContext.Current.CancellationToken);

        Assert.Single(recorder.Forwarded);
        Assert.Equal(["report relay first: herdr answered " + herdrError], recorder.Logged);
    }

    [Fact]
    public async Task UnreachableHerdrIsLoggedNotThrown()
    {
        var recorder = new Recorder();
        var server = recorder.Server((_, _) => throw new SocketException((int)SocketError.ConnectionRefused));

        await server.HandleLineAsync("""{"id":"b","method":"pane.report_agent"}""", TestContext.Current.CancellationToken);

        Assert.Contains("herdr is unreachable", Assert.Single(recorder.Logged));
    }

    [Fact]
    public async Task SuccessIsNotLogged()
    {
        var recorder = new Recorder();
        await recorder.Server().HandleLineAsync("""{"id":"b","method":"pane.report_agent"}""", TestContext.Current.CancellationToken);
        Assert.Empty(recorder.Logged);
    }

    /// <summary>
    /// Each <c>cat</c> ends when the FIFO's writers close it; the relay reopens it at once
    /// without re-creating the FIFO.
    /// </summary>
    [Fact]
    public async Task ReaderIsReopenedAfterEof()
    {
        var recorder = new Recorder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reads = 0;
        var ensured = 0;
        string[][] batches =
        [
            ["""{"id":"1","method":"pane.report_agent"}"""],
            ["""{"id":"2","method":"pane.report_agent"}""", """{"id":"3","method":"pane.report_agent"}"""],
            ["""{"id":"4","method":"pane.report_agent"}"""],
        ];

        await recorder.Server().RunAsync(async (onLine, _) =>
        {
            var batch = batches[reads++];
            foreach (var line in batch) await onLine(line);
            if (reads == batches.Length) await stop.CancelAsync();
            return 0;
        }, _ => { ensured++; return Task.CompletedTask; }, TimeSpan.FromSeconds(30), stop.Token);

        Assert.Equal(3, reads);
        Assert.Equal(1, ensured);
        Assert.Equal(["1", "2", "3", "4"], recorder.Forwarded.Select(f => JsonDocument.Parse(f).RootElement.GetProperty("id").GetString()));
    }

    /// <summary>A failed reader -- the sandbox stopped, the FIFO gone -- waits, re-creates the FIFO, then reopens.</summary>
    [Fact]
    public async Task FailedReaderRecreatesTheFifoBeforeReopening()
    {
        var recorder = new Recorder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var codes = new Queue<int>([1, 0]);
        var ensured = 0;

        await recorder.Server().RunAsync((_, _) =>
        {
            var code = codes.Dequeue();
            if (codes.Count == 0) stop.Cancel();
            return Task.FromResult(code);
        }, _ => { ensured++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(10), stop.Token);

        Assert.Equal(2, ensured);
        Assert.Contains("FIFO reader exited 1", Assert.Single(recorder.Logged));
    }

    /// <summary>
    /// The sandbox stopping makes the reader and the FIFO re-creation throw rather than
    /// return a code; the relay backs off and keeps trying, and resumes once it is back.
    /// </summary>
    [Fact]
    public async Task ReaderRecoversAfterTheSandboxGoesAwayAndComesBack()
    {
        var recorder = new Recorder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = true;
        var reads = 0;
        var ensured = 0;
        var delays = new List<TimeSpan>();

        await recorder.Server().RunAsync(async (onLine, _) =>
        {
            reads++;
            if (!running) throw new WipException("Sandbox first: sandbox is not running; run sandbox create first");
            if (reads == 1)
            {
                await onLine("""{"id":"before","method":"pane.report_agent"}""");
                running = false;
                return 137;
            }
            await onLine("""{"id":"after","method":"pane.report_agent"}""");
            await stop.CancelAsync();
            return 0;
        }, _ =>
        {
            ensured++;
            if (ensured > 1 && !running)
            {
                if (ensured == 3) running = true;
                throw new WipException("Sandbox probe failed (exit 1)");
            }
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(5), stop.Token, maxRetryDelay: TimeSpan.FromMilliseconds(20));

        Assert.Equal(["before", "after"], recorder.Forwarded.Select(f => JsonDocument.Parse(f).RootElement.GetProperty("id").GetString()));
        Assert.Equal(4, ensured);
        Assert.Contains(recorder.Logged, l => l.Contains("FIFO reader exited 137"));
        Assert.Contains(recorder.Logged, l => l.Contains("Sandbox probe failed"));
        Assert.All(recorder.Logged, l => Assert.Contains("retrying in", l));
    }

    [Fact]
    public async Task BackoffDoublesToItsCeilingAndResetsAfterACleanRead()
    {
        var recorder = new Recorder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var codes = new Queue<int>([1, 1, 1, 1, 0, 1, 0]);

        await recorder.Server().RunAsync((_, _) =>
        {
            var code = codes.Dequeue();
            if (codes.Count == 0) stop.Cancel();
            return Task.FromResult(code);
        }, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(2), stop.Token, maxRetryDelay: TimeSpan.FromMilliseconds(8));

        Assert.Equal(["0.002s", "0.004s", "0.008s", "0.008s", "0.002s"],
            recorder.Logged.Select(l => l[(l.LastIndexOf(' ') + 1)..]));
    }

    /// <summary>Only the first FIFO creation fails fast, so a misconfiguration is reported at once.</summary>
    [Fact]
    public async Task FifoCreationFailureStopsTheRelay()
    {
        var recorder = new Recorder();
        await Assert.ThrowsAsync<WipException>(() => recorder.Server().RunAsync(
            (_, _) => Task.FromResult(0), _ => throw new WipException("no FIFO"), TimeSpan.Zero, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A second relay for the same sandbox refuses to start while the first holds the lock,
    /// so no report is split between two readers or stamped with the wrong pane.
    /// </summary>
    [Fact]
    public void SecondRelayForTheSameSandboxRefusesToStart()
    {
        var directory = Directory.CreateTempSubdirectory("wip-relay-");
        try
        {
            var path = ReportRelay.LockPath(directory.FullName, "first");
            Assert.Equal(Path.Combine(directory.FullName, ".wip", "report-relay", "first.lock"), path);

            using (ReportRelay.AcquireRelayLock(path, "first"))
            {
                var refused = Assert.Throws<WipException>(() => ReportRelay.AcquireRelayLock(path, "first"));
                Assert.Contains("Sandbox first", refused.Message);
                Assert.Contains("already running", refused.Message);

                // The lock is per sandbox: another sandbox's relay is unaffected.
                using var other = ReportRelay.AcquireRelayLock(ReportRelay.LockPath(directory.FullName, "second"), "second");
            }

            // Released when the first relay exits.
            using var again = ReportRelay.AcquireRelayLock(path, "first");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void RelayLockFileIsOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix file modes do not apply on Windows");
        var directory = Directory.CreateTempSubdirectory("wip-relay-");
        try
        {
            var path = ReportRelay.LockPath(directory.FullName, "first");
            using (ReportRelay.AcquireRelayLock(path, "first"))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The real path on Linux: the shim writes into a real FIFO, <c>cat</c> reads it the way
    /// <c>sandbox exec</c> does, and the reader is reopened after each EOF.
    /// </summary>
    [Fact]
    public async Task ShimLinesThroughARealFifoSurviveReopening()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "mkfifo and the shim need Linux");
        var cancellation = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("wip-fifo-");
        try
        {
            var fifo = Path.Combine(directory.FullName, "report.fifo");
            Assert.Equal(0, Sh($"mkfifo -m 600 '{fifo}'"));
            var recorder = new Recorder();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            stop.CancelAfter(TimeSpan.FromSeconds(60));
            var opened = 0;

            var relay = recorder.Server().RunAsync(async (onLine, token) =>
            {
                Interlocked.Increment(ref opened);
                using var cat = Process.Start(new ProcessStartInfo("cat") { ArgumentList = { fifo }, RedirectStandardOutput = true })!;
                using var kill = token.Register(() => { try { cat.Kill(); } catch (InvalidOperationException) { } });
                await ReportRelay.PumpLinesAsync(cat.StandardOutput, onLine, token);
                await cat.WaitForExitAsync(token);
                return cat.ExitCode;
            }, _ => Task.CompletedTask, TimeSpan.FromSeconds(1), stop.Token);

            string[] states = ["working", "blocked", "idle"];
            for (var i = 0; i < states.Length; i++)
            {
                using var shim = Process.Start(new ProcessStartInfo("/bin/sh")
                {
                    ArgumentList = { ShimPath, "report-agent", "--agent", "claude-code", "--state", states[i] },
                    Environment = { ["WIP_REPORT_FIFO"] = fifo },
                })!;
                await shim.WaitForExitAsync(cancellation);
                Assert.Equal(0, shim.ExitCode);
                while (recorder.Forwarded.Count < i + 1) await Task.Delay(10, cancellation);
            }

            await stop.CancelAsync();
            await relay;
            Assert.Equal(states, recorder.Forwarded.Select(f =>
                JsonDocument.Parse(f).RootElement.GetProperty("params").GetProperty("state").GetString()));
            Assert.All(recorder.Forwarded, f => Assert.Contains("\"pane_id\":\"pane-7\",\"source\":\"wip:first\"", f));
            Assert.True(opened >= 3, $"reader opened {opened} times");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Herdr on Windows serves a named pipe named by HERDR_SOCKET_PATH. .NET maps the same
    /// API onto a unix socket elsewhere, so the client is exercised here on any host.
    /// </summary>
    [Fact]
    public async Task NamedPipeUpstreamExchangesOneLine()
    {
        var pipeName = "wip-relay-" + Guid.NewGuid().ToString("N")[..12];
        var cancellation = TestContext.Current.CancellationToken;
        await using var herdr = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var herdrTask = Task.Run(async () =>
        {
            await herdr.WaitForConnectionAsync(cancellation);
            using var reader = new StreamReader(herdr, leaveOpen: true);
            var request = await reader.ReadLineAsync(cancellation);
            await herdr.WriteAsync(Encoding.UTF8.GetBytes(Ok + "\n"), cancellation);
            return request;
        }, cancellation);

        var reply = await ReportRelayServer.NamedPipe(pipeName)("{\"id\":\"b\",\"method\":\"pane.report_agent\",\"params\":{}}", cancellation);

        Assert.Equal(Ok, reply);
        Assert.Equal("{\"id\":\"b\",\"method\":\"pane.report_agent\",\"params\":{}}", await herdrTask);
    }

    [Fact]
    public async Task UnixSocketUpstreamExchangesOneLine()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("wip-relay-");
        try
        {
            var herdrPath = Path.Combine(directory.FullName, "h.sock");
            using var herdr = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            herdr.Bind(new UnixDomainSocketEndPoint(herdrPath));
            herdr.Listen(1);
            var herdrTask = Task.Run(async () =>
            {
                using var connection = await herdr.AcceptAsync(cancellation);
                await using var stream = new NetworkStream(connection);
                using var reader = new StreamReader(stream);
                var request = await reader.ReadLineAsync(cancellation);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(Ok + "\n"), cancellation);
                return request;
            }, cancellation);

            Assert.Equal(Ok, await ReportRelayServer.UnixSocket(herdrPath)("{}", cancellation));
            Assert.Equal("{}", await herdrTask);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A Herdr that accepts and then never answers costs one deadline, logged as
    /// unreachable, not a report queue held up until the relay stops.
    /// </summary>
    [Fact]
    public async Task UnixSocketUpstreamThatNeverRepliesTimesOutAsUnreachable()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("wip-relay-");
        try
        {
            var herdrPath = Path.Combine(directory.FullName, "h.sock");
            using var herdr = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            herdr.Bind(new UnixDomainSocketEndPoint(herdrPath));
            herdr.Listen(4);
            var accepted = new List<Socket>();
            _ = Task.Run(async () =>
            {
                while (true) accepted.Add(await herdr.AcceptAsync(cancellation));
            }, cancellation);
            var upstream = ReportRelayServer.UnixSocket(herdrPath, TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAsync<IOException>(() => upstream("{}", cancellation));

            var recorder = new Recorder();
            await recorder.Server(upstream).HandleLineAsync("""{"id":"b","method":"pane.report_agent","params":{}}""", cancellation);
            Assert.Contains("herdr is unreachable", Assert.Single(recorder.Logged));
            foreach (var socket in accepted) socket.Dispose();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NamedPipeUpstreamThatNeverRepliesTimesOutAsUnreachable()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var pipeName = "wip-relay-" + Guid.NewGuid().ToString("N")[..12];
        await using var herdr = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepting = herdr.WaitForConnectionAsync(cancellation);
        var recorder = new Recorder();

        await recorder.Server(ReportRelayServer.NamedPipe(pipeName, TimeSpan.FromMilliseconds(200)))
            .HandleLineAsync("""{"id":"b","method":"pane.report_agent","params":{}}""", cancellation);

        await accepting;
        Assert.Contains("herdr is unreachable", Assert.Single(recorder.Logged));
    }

    /// <summary>The deadline covers connect too: a pipe nobody serves is not waited on forever.</summary>
    [Fact]
    public async Task NamedPipeUpstreamWithNoServerTimesOut()
    {
        var upstream = ReportRelayServer.NamedPipe("wip-relay-absent-" + Guid.NewGuid().ToString("N")[..12], TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<IOException>(() => upstream("{}", TestContext.Current.CancellationToken));
    }

    /// <summary>Stopping the relay is not a Herdr failure, so it is not reported as one.</summary>
    [Fact]
    public async Task RelayShutdownCancelsTheUpstreamCallRatherThanTimingOut()
    {
        var upstream = ReportRelayServer.NamedPipe("wip-relay-absent-" + Guid.NewGuid().ToString("N")[..12], TimeSpan.FromMinutes(5));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upstream("{}", stop.Token));
    }
}
