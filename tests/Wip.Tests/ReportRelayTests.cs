using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Wip.Execution;

namespace Wip.Tests;

public class ReportRelayTests
{
    private const string Source = "wip:first";

    [Theory]
    [InlineData("report-agent")]
    [InlineData("report-agent-session")]
    public void AllowedMethodsAreForwardedWithTheRelaySource(string method)
    {
        var decision = ReportRelay.Filter($$$"""{"id":"r1","method":"{{{method}}}","params":{"pane_id":"1-2","state":"working"}}""", Source);

        Assert.True(decision.Accepted);
        using var forwarded = JsonDocument.Parse(decision.Forward!);
        Assert.Equal("r1", forwarded.RootElement.GetProperty("id").GetString());
        Assert.Equal(method, forwarded.RootElement.GetProperty("method").GetString());
        var parameters = forwarded.RootElement.GetProperty("params");
        Assert.Equal("1-2", parameters.GetProperty("pane_id").GetString());
        Assert.Equal("working", parameters.GetProperty("state").GetString());
        Assert.Equal(Source, parameters.GetProperty("source").GetString());
    }

    /// <summary>A sandbox cannot report under another sandbox's name.</summary>
    [Fact]
    public void SpoofedSourceIsReplaced()
    {
        var decision = ReportRelay.Filter("""{"method":"report-agent","params":{"source":"wip:other"}}""", Source);

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
    [InlineData("""{"id":"r1","method":"REPORT-AGENT"}""")]
    [InlineData("""{"id":"r1","method":"report-agent ","params":{}}""")]
    [InlineData("""{"id":"r1","params":{}}""")]
    [InlineData("""{"id":"r1","method":["report-agent"]}""")]
    [InlineData("""{"id":"r1","method":"report-agent","params":[]}""")]
    [InlineData("""{"id":"r1","method":"report-agent","subscribe":true}""")]
    [InlineData("""{"id":7,"method":"report-agent"}""")]
    [InlineData("""[{"method":"report-agent"}]""")]
    [InlineData("""{"method":"report-agent"}{"method":"pane.read"}""")]
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
        Assert.False(ReportRelay.Filter($$$"""{"method":"report-agent","params":{"note":"{{{padding}}}"}}""", Source).Accepted);
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
            return Task.FromResult("""{"id":"ok","result":{}}""");
        });
        var input = "{\"id\":\"a\",\"method\":\"pane.read\"}\n\n{\"id\":\"b\",\"method\":\"report-agent\",\"params\":{}}\r\n";
        var stream = new DuplexStream(input);

        await server.ServeAsync(stream, TestContext.Current.CancellationToken);

        var replies = stream.Written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, replies.Length);
        using (var refused = JsonDocument.Parse(replies[0]))
        {
            Assert.Equal("a", refused.RootElement.GetProperty("id").GetString());
            Assert.Equal("relay_refused", refused.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.Equal("""{"id":"ok","result":{}}""", replies[1]);
        Assert.Equal(["""{"id":"b","method":"report-agent","params":{"source":"wip:first"}}"""], forwarded);
    }

    [Fact]
    public async Task UnreachableHerdrIsReportedToTheSandbox()
    {
        var server = new ReportRelayServer(Source, (_, _) => throw new SocketException((int)SocketError.ConnectionRefused));
        var stream = new DuplexStream("{\"id\":\"b\",\"method\":\"report-agent\"}\n");

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
                await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"b\",\"result\":{}}\n"), cancellation.Token);
            }, cancellation.Token);

            var server = new ReportRelayServer(Source, ReportRelayServer.UnixSocket(herdrPath));
            var relayTask = server.ListenAsync(relayPath, cancellation.Token);
            while (!File.Exists(relayPath)) await Task.Delay(10, cancellation.Token);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(relayPath), cancellation.Token);
            await using var clientStream = new NetworkStream(client);
            using var clientReader = new StreamReader(clientStream);
            await clientStream.WriteAsync(Encoding.UTF8.GetBytes(
                "{\"id\":\"a\",\"method\":\"workspace.create\"}\n{\"id\":\"b\",\"method\":\"report-agent-session\",\"params\":{\"pane_id\":\"1-2\"}}\n"),
                cancellation.Token);

            Assert.Contains("relay_refused", await clientReader.ReadLineAsync(cancellation.Token));
            Assert.Equal("{\"id\":\"b\",\"result\":{}}", await clientReader.ReadLineAsync(cancellation.Token));
            await herdrTask;
            Assert.Equal(["{\"id\":\"b\",\"method\":\"report-agent-session\",\"params\":{\"pane_id\":\"1-2\",\"source\":\"wip:first\"}}"], received);

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
