using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
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
        var server = new ReportRelayServer(Source, (line, _) =>
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
        var server = new ReportRelayServer(Source, (_, _) => Task.FromResult(herdrError));
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
        var server = new ReportRelayServer(Source, (_, _) => throw new SocketException((int)SocketError.ConnectionRefused));
        var stream = new DuplexStream("{\"id\":\"b\",\"method\":\"pane.report_agent\"}\n");

        await server.ServeAsync(stream, TestContext.Current.CancellationToken);

        Assert.Contains("herdr is unreachable", stream.Written);
    }

    [Fact]
    public async Task UnterminatedOversizedLineClosesTheConnection()
    {
        var forwarded = 0;
        var server = new ReportRelayServer(Source, (_, _) => { forwarded++; return Task.FromResult("{}"); });
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

            var server = new ReportRelayServer(Source, ReportRelayServer.UnixSocket(herdrPath));
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
