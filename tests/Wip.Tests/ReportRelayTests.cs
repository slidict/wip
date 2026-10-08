using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Runtime.Versioning;
using System.Text.Json;
using Wip.Execution;

namespace Wip.Tests;

public class ReportRelayTests
{
    private const string Source = "wip:first";

    [Theory]
    [InlineData("pane.report_agent")]
    [InlineData("pane.report_agent_session")]
    public void AllowedMethodsAreForwardedWithTheRelaySource(string method)
    {
        var decision = ReportRelay.Filter($$$"""
            {"id":"r1","method":"{{{method}}}","params":{"pane_id":"1-2","agent":"claude","state":"working","seq":3,"agent_session_id":"s-1"}}
            """, Source);

        Assert.True(decision.Accepted);
        using var forwarded = JsonDocument.Parse(decision.Forward!);
        Assert.Equal("r1", forwarded.RootElement.GetProperty("id").GetString());
        Assert.Equal(method, forwarded.RootElement.GetProperty("method").GetString());
        var parameters = forwarded.RootElement.GetProperty("params");
        Assert.Equal("1-2", parameters.GetProperty("pane_id").GetString());
        Assert.Equal("claude", parameters.GetProperty("agent").GetString());
        Assert.Equal("working", parameters.GetProperty("state").GetString());
        Assert.Equal(3, parameters.GetProperty("seq").GetInt32());
        Assert.Equal("s-1", parameters.GetProperty("agent_session_id").GetString());
        Assert.Equal(Source, parameters.GetProperty("source").GetString());
    }

    /// <summary>A sandbox cannot report under another sandbox's name.</summary>
    [Fact]
    public void SpoofedSourceIsReplaced()
    {
        var decision = ReportRelay.Filter("""{"method":"pane.report_agent","params":{"source":"wip:other"}}""", Source);

        Assert.True(decision.Accepted);
        using var forwarded = JsonDocument.Parse(decision.Forward!);
        var parameters = forwarded.RootElement.GetProperty("params");
        Assert.Equal(Source, parameters.GetProperty("source").GetString());
        Assert.Single(parameters.EnumerateObject());
    }

    [Theory]
    [InlineData("""{"id":"r1","method":"pane.send_input","params":{"pane_id":"1-1","text":"rm -rf /"}}""")]
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
    [InlineData("""{"id":7,"method":"pane.report_agent"}""")]
    [InlineData("""[{"method":"pane.report_agent"}]""")]
    [InlineData("""{"method":"pane.report_agent"}{"method":"pane.read"}""")]
    [InlineData("not json")]
    public void EverythingElseIsRefused(string line)
    {
        var decision = ReportRelay.Filter(line, Source);

        Assert.False(decision.Accepted);
        Assert.Null(decision.Forward);
        Assert.NotNull(decision.Error);
    }

    [Fact]
    public void OversizedMessageIsRefused()
    {
        var padding = new string('x', ReportRelay.MaxLineBytes);
        Assert.False(ReportRelay.Filter($$$"""{"method":"pane.report_agent","params":{"note":"{{{padding}}}"}}""", Source).Accepted);
    }

    [Fact]
    public void ExecEnvironmentExportsSocketSourceAndPresentPassThroughOnly()
    {
        var host = new Dictionary<string, string> { ["HERDR_PANE_ID"] = "1-2", ["HERDR_SOCKET_PATH"] = "/host/herdr.sock" };

        var environment = ReportRelay.ExecEnvironment("first", name => host.GetValueOrDefault(name));

        Assert.Equal(
            [
                new(ReportRelay.SocketVariable, "/run/wip/herdr/report.sock"),
                new(ReportRelay.SourceVariable, Source),
                new("HERDR_PANE_ID", "1-2"),
            ],
            environment);
        Assert.DoesNotContain(environment, e => e.Key == "HERDR_SOCKET_PATH");
        Assert.Equal(2, ReportRelay.ExecEnvironment("first", _ => null).Count);
        Assert.Equal(2, ReportRelay.ExecEnvironment("first", _ => "1-2\n-e X=1").Count);
    }

    [Fact]
    public async Task ServerAnswersEveryLineAndForwardsOnlyAllowedOnes()
    {
        var forwarded = new List<string>();
        var server = new ReportRelayServer("first", (line, _) =>
        {
            forwarded.Add(line);
            return Task.FromResult("""{"id":"b","result":{"type":"ok"}}""");
        });
        var input = "{\"id\":\"a\",\"method\":\"pane.read\"}\n\n{\"id\":\"b\",\"method\":\"pane.report_agent\",\"params\":{}}\r\n";
        var stream = new DuplexStream(input);

        await server.ServeAsync(stream, TestContext.Current.CancellationToken);

        var replies = stream.Written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, replies.Length);
        using (var refused = JsonDocument.Parse(replies[0]))
        {
            Assert.Equal("a", refused.RootElement.GetProperty("id").GetString());
            Assert.Equal("relay_refused", refused.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.Equal("""{"id":"b","result":{"type":"ok"}}""", replies[1]);
        Assert.Equal(["""{"id":"b","method":"pane.report_agent","params":{"source":"wip:first"}}"""], forwarded);
    }

    /// <summary>
    /// Herdr blanks the id on errors; the relay hands the reply back as-is rather than
    /// re-shaping it, so the sandbox sees exactly what Herdr said.
    /// </summary>
    [Fact]
    public async Task HerdrErrorWithBlankedIdIsPassedBackUnchanged()
    {
        const string herdrError = """{"id":"","error":{"code":"invalid_request","message":"missing field `state`"}}""";
        var server = new ReportRelayServer("first", (_, _) => Task.FromResult(herdrError));
        var stream = new DuplexStream("{\"id\":\"b\",\"method\":\"pane.report_agent\",\"params\":{\"pane_id\":\"1-2\"}}\n");

        await server.ServeAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(herdrError + "\n", stream.Written);
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
            await herdr.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"b\",\"result\":{\"type\":\"ok\"}}\n"), cancellation);
            return request;
        }, cancellation);

        var reply = await ReportRelayServer.NamedPipe(pipeName)("{\"id\":\"b\",\"method\":\"pane.report_agent\",\"params\":{}}", cancellation);

        Assert.Equal("{\"id\":\"b\",\"result\":{\"type\":\"ok\"}}", reply);
        Assert.Equal("{\"id\":\"b\",\"method\":\"pane.report_agent\",\"params\":{}}", await herdrTask);
    }

    [Fact]
    public async Task UnreachableHerdrIsReportedToTheSandbox()
    {
        var server = new ReportRelayServer("first", (_, _) => throw new SocketException((int)SocketError.ConnectionRefused));
        var stream = new DuplexStream("{\"id\":\"b\",\"method\":\"pane.report_agent\"}\n");

        await server.ServeAsync(stream, TestContext.Current.CancellationToken);

        Assert.Contains("herdr is unreachable", stream.Written);
    }

    [Fact]
    public async Task UnterminatedOversizedLineClosesTheConnection()
    {
        var forwarded = 0;
        var server = new ReportRelayServer("first", (_, _) => { forwarded++; return Task.FromResult("{}"); });
        var stream = new DuplexStream(new string('x', ReportRelay.MaxLineBytes * 2));

        await server.ServeAsync(stream, TestContext.Current.CancellationToken);

        Assert.Contains("message too large", stream.Written);
        Assert.Equal(0, forwarded);
    }

    /// <summary>The real path: sandbox client -> relay socket -> Herdr's socket, both unix sockets.</summary>
    [Fact]
    public async Task RelaysOverUnixSocketsEndToEnd()
    {
        var directory = Directory.CreateTempSubdirectory("wip-relay-");
        try
        {
            var herdrPath = Path.Combine(directory.FullName, "h.sock");
            var relayPath = Path.Combine(directory.FullName, ReportRelay.SocketName);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            var received = new List<string>();

            using var herdr = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            herdr.Bind(new UnixDomainSocketEndPoint(herdrPath));
            herdr.Listen(1);
            var herdrTask = Task.Run(async () =>
            {
                using var connection = await herdr.AcceptAsync(cancellation.Token);
                await using var stream = new NetworkStream(connection);
                using var reader = new StreamReader(stream);
                received.Add((await reader.ReadLineAsync(cancellation.Token))!);
                await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"b\",\"result\":{\"type\":\"ok\"}}\n"), cancellation.Token);
            }, cancellation.Token);

            var server = new ReportRelayServer("first", ReportRelayServer.UnixSocket(herdrPath));
            var relayTask = server.ListenAsync(relayPath, cancellation.Token);
            while (!File.Exists(relayPath)) await Task.Delay(10, cancellation.Token);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(relayPath), cancellation.Token);
            await using var clientStream = new NetworkStream(client);
            using var clientReader = new StreamReader(clientStream);
            await clientStream.WriteAsync(Encoding.UTF8.GetBytes(
                "{\"id\":\"a\",\"method\":\"workspace.create\"}\n{\"id\":\"b\",\"method\":\"pane.report_agent_session\",\"params\":{\"pane_id\":\"1-2\"}}\n"),
                cancellation.Token);

            Assert.Contains("relay_refused", await clientReader.ReadLineAsync(cancellation.Token));
            Assert.Equal("{\"id\":\"b\",\"result\":{\"type\":\"ok\"}}", await clientReader.ReadLineAsync(cancellation.Token));
            await herdrTask;
            Assert.Equal(["{\"id\":\"b\",\"method\":\"pane.report_agent_session\",\"params\":{\"pane_id\":\"1-2\",\"source\":\"wip:first\"}}"], received);

            await cancellation.CancelAsync();
            await relayTask;
            Assert.False(File.Exists(relayPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static readonly ReportUpstream Ok = (_, _) => Task.FromResult("""{"id":"b","result":{"type":"ok"}}""");

    private static async Task<string?> ReportAsync(string socketPath, CancellationToken cancellation)
    {
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellation);
        await using var stream = new NetworkStream(client);
        using var reader = new StreamReader(stream);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"b\",\"method\":\"pane.report_agent\",\"params\":{}}\n"), cancellation);
        return await reader.ReadLineAsync(cancellation);
    }

    /// <summary>
    /// The socket and its directory get owner-only modes set explicitly, not whatever the
    /// umask leaves, so another local user cannot report under this sandbox's source.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task ListenerSetsOwnerOnlyModesRegardlessOfExistingPermissions()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix file modes do not apply on Windows");
        var cancellation = TestContext.Current.CancellationToken;
        var parent = Directory.CreateTempSubdirectory("wip-relay-");
        try
        {
            var directory = Path.Combine(parent.FullName, "first");
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
            var socketPath = Path.Combine(directory, ReportRelay.SocketName);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

            var relay = new ReportRelayServer("first", Ok).ListenAsync(socketPath, stop.Token);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socketPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socketPath + ".lock"));
            await stop.CancelAsync();
            await relay;
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void EnsurePrivateDirectoryTightensAnExistingDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix file modes do not apply on Windows");
        var directory = Directory.CreateTempSubdirectory("wip-relay-");
        try
        {
            File.SetUnixFileMode(directory.FullName, (UnixFileMode)0b111_101_101);
            ReportRelay.EnsurePrivateDirectory(directory.FullName);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A second relay for the same sandbox must not take the socket from a live one; it
    /// refuses, the first keeps serving, and only the first removes the socket on exit.
    /// </summary>
    [Fact]
    public async Task SecondInstanceIsRefusedAndTheFirstKeepsItsSocket()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("wip-relay-");
        try
        {
            var socketPath = Path.Combine(directory.FullName, ReportRelay.SocketName);
            using var stopFirst = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var first = new ReportRelayServer("first", Ok).ListenAsync(socketPath, stopFirst.Token);

            var refused = await Assert.ThrowsAsync<WipException>(() =>
                new ReportRelayServer("first", Ok).ListenAsync(socketPath, cancellation));

            Assert.Contains("Sandbox first", refused.Message);
            Assert.Contains("already running", refused.Message);
            Assert.True(File.Exists(socketPath));
            Assert.Equal("""{"id":"b","result":{"type":"ok"}}""", await ReportAsync(socketPath, cancellation));

            await stopFirst.CancelAsync();
            await first;
            Assert.False(File.Exists(socketPath));

            // Once the first has stopped, the lock is free again.
            using var stopThird = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var third = new ReportRelayServer("first", Ok).ListenAsync(socketPath, stopThird.Token);
            Assert.Equal("""{"id":"b","result":{"type":"ok"}}""", await ReportAsync(socketPath, cancellation));
            await stopThird.CancelAsync();
            await third;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StaleSocketFromAGoneRelayIsReplaced()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("wip-relay-");
        try
        {
            var socketPath = Path.Combine(directory.FullName, ReportRelay.SocketName);
            // .NET unlinks a socket it bound when disposed, so a crashed relay's leftover is
            // stood in for by a plain file at the path; either way nothing holds the lock.
            File.WriteAllText(socketPath, "");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

            var relay = new ReportRelayServer("first", Ok).ListenAsync(socketPath, stop.Token);

            Assert.Equal("""{"id":"b","result":{"type":"ok"}}""", await ReportAsync(socketPath, cancellation));
            await stop.CancelAsync();
            await relay;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A Herdr that accepts and then never answers costs one deadline, reported as
    /// unreachable, not a connection held until the relay stops.
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
            var silent = Task.Run(async () =>
            {
                while (true) accepted.Add(await herdr.AcceptAsync(cancellation));
            }, cancellation);
            var upstream = ReportRelayServer.UnixSocket(herdrPath, TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAsync<IOException>(() => upstream("{}", cancellation));

            var stream = new DuplexStream("{\"id\":\"b\",\"method\":\"pane.report_agent\",\"params\":{}}\n");
            await new ReportRelayServer("first", upstream).ServeAsync(stream, cancellation);
            Assert.Contains("herdr is unreachable", stream.Written);
            Assert.Contains("\"id\":\"b\"", stream.Written);
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
        var upstream = ReportRelayServer.NamedPipe(pipeName, TimeSpan.FromMilliseconds(200));

        var stream = new DuplexStream("{\"id\":\"b\",\"method\":\"pane.report_agent\",\"params\":{}}\n");
        await new ReportRelayServer("first", upstream).ServeAsync(stream, cancellation);

        await accepting;
        Assert.Contains("herdr is unreachable", stream.Written);
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

    /// <summary>Reads from a fixed input and records what is written back.</summary>
    private sealed class DuplexStream(string input) : Stream
    {
        private readonly MemoryStream _input = new(Encoding.UTF8.GetBytes(input));
        private readonly MemoryStream _output = new();

        public string Written => Encoding.UTF8.GetString(_output.ToArray());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
    }
}
