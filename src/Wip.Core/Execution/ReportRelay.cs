using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Wip.Execution;

/// <summary>The relay's verdict on one line: the line to forward, or why it was refused.</summary>
public sealed record ReportRelayDecision(bool Accepted, string? Forward, string? Id, string? Error);

/// <summary>
/// A restricted, host-side channel through which a CLI inside a sandbox reports its own agent
/// state to Herdr.
/// </summary>
/// <remarks>
/// <para>
/// The sandbox never sees Herdr's socket. It sees only a relay socket in a host directory
/// bound read-only at <see cref="MountPath"/>, and the relay forwards exactly the two report
/// methods in <see cref="AllowedMethods"/>. Everything else -- reading panes, sending input,
/// creating workspaces -- is refused at the relay, so a sandboxed agent cannot drive the
/// rest of the Herdr API.
/// </para>
/// <para>
/// The relay also stamps <c>params.source</c> with its own source id, so one sandbox cannot
/// report under another's name.
/// </para>
/// </remarks>
public static class ReportRelay
{
    /// <summary>Fixed in-sandbox directory the host relay directory is bound to.</summary>
    public const string MountPath = "/run/wip/herdr";

    public const string SocketName = "report.sock";

    /// <summary>Fixed in-sandbox path of the relay socket.</summary>
    public const string SocketPath = MountPath + "/" + SocketName;

    public const string SocketVariable = "WIP_REPORT_SOCKET";
    public const string SourceVariable = "WIP_REPORT_SOURCE";

    /// <summary>Host variables handed through unchanged when present.</summary>
    public static readonly IReadOnlyList<string> PassThroughVariables = ["HERDR_PANE_ID"];

    public static readonly IReadOnlySet<string> AllowedMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "report-agent",
        "report-agent-session",
    };

    /// <summary>Longest accepted line; reports are small, so anything bigger is refused.</summary>
    public const int MaxLineBytes = 16 * 1024;

    /// <summary>Host directory bound into the sandbox, kept beside the config under <c>.wip</c>.</summary>
    public static string HostDirectory(string configDirectory, string sandbox) =>
        Path.Combine(configDirectory, ".wip", "report-relay", sandbox);

    public static string SourceId(string sandbox) => "wip:" + sandbox;

    /// <summary>
    /// Variables an exec into a relay-enabled sandbox exports: the socket path, the source id,
    /// and the pass-through variables the host actually has. Values with control characters
    /// are dropped rather than passed, since they cannot be a pane id.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ExecEnvironment(string sandbox, Func<string, string?> host)
    {
        var result = new List<KeyValuePair<string, string>>
        {
            new(SocketVariable, SocketPath),
            new(SourceVariable, SourceId(sandbox)),
        };
        foreach (var name in PassThroughVariables)
        {
            if (host(name) is { Length: > 0 } value && !value.Any(char.IsControl)) result.Add(new(name, value));
        }
        return result;
    }

    /// <summary>Checks one line from the sandbox and, when allowed, rewrites it for Herdr.</summary>
    public static ReportRelayDecision Filter(string line, string source)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes) return Refuse(null, "message too large");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return Refuse(null, "message must be one JSON object per line");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Refuse(null, "message must be a JSON object");
            string? id = null;
            if (root.TryGetProperty("id", out var idElement))
            {
                if (idElement.ValueKind != JsonValueKind.String) return Refuse(null, "id must be a string");
                id = idElement.GetString();
            }
            if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
                !AllowedMethods.Contains(method.GetString()!))
                return Refuse(id, "method not allowed through the report relay");
            var parameters = default(JsonElement);
            if (root.TryGetProperty("params", out var p))
            {
                if (p.ValueKind != JsonValueKind.Object) return Refuse(id, "params must be an object");
                parameters = p;
            }
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("id" or "method" or "params")) return Refuse(id, "unexpected field " + property.Name);
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (id is not null) writer.WriteString("id", id);
                writer.WriteString("method", method.GetString());
                writer.WriteStartObject("params");
                if (parameters.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in parameters.EnumerateObject())
                    {
                        if (property.Name != "source") property.WriteTo(writer);
                    }
                }
                writer.WriteString("source", source);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return new(true, Encoding.UTF8.GetString(buffer.ToArray()), id, null);
        }
    }

    /// <summary>The error line returned to the sandbox for a refused message.</summary>
    public static string ErrorLine(string? id, string message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (id is null) writer.WriteNull("id"); else writer.WriteString("id", id);
            writer.WriteStartObject("error");
            writer.WriteString("code", "relay_refused");
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static ReportRelayDecision Refuse(string? id, string error) => new(false, null, id, error);
}

/// <summary>Forwards one accepted line to Herdr and returns Herdr's one-line reply.</summary>
public delegate Task<string> ReportUpstream(string line, CancellationToken cancellation);

/// <summary>
/// Listens on the relay socket and answers each line: refused lines get an error line back,
/// accepted ones are forwarded through <see cref="ReportUpstream"/>.
/// </summary>
public sealed class ReportRelayServer(string source, ReportUpstream upstream)
{
    /// <summary>Handles one connection until the peer closes it or sends an oversized line.</summary>
    public async Task ServeAsync(Stream stream, CancellationToken cancellation)
    {
        var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        while (!cancellation.IsCancellationRequested)
        {
            var line = await ReadLineAsync(reader, cancellation);
            if (line is null) return;
            if (line.Length > ReportRelay.MaxLineBytes)
            {
                await writer.WriteLineAsync(ReportRelay.ErrorLine(null, "message too large"));
                return;
            }
            if (line.Trim().Length == 0) continue;
            var decision = ReportRelay.Filter(line, source);
            string reply;
            if (!decision.Accepted)
            {
                reply = ReportRelay.ErrorLine(decision.Id, decision.Error!);
            }
            else
            {
                try
                {
                    reply = await upstream(decision.Forward!, cancellation);
                }
                catch (Exception e) when (e is IOException or SocketException)
                {
                    reply = ReportRelay.ErrorLine(decision.Id, "herdr is unreachable");
                }
            }
            await writer.WriteLineAsync(reply);
        }
    }

    /// <summary>Accepts connections on <paramref name="socketPath"/> until cancelled.</summary>
    public async Task ListenAsync(string socketPath, CancellationToken cancellation)
    {
        // The directory is wip's own, so a socket left by an earlier relay is stale.
        if (File.Exists(socketPath)) File.Delete(socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(16);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(cancellation);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    using var connection = client;
                    await using var stream = new NetworkStream(connection, ownsSocket: false);
                    try
                    {
                        await ServeAsync(stream, cancellation);
                    }
                    catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
                    {
                        // One sandbox client hanging up must not stop the relay.
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            try { File.Delete(socketPath); } catch (IOException) { }
        }
    }

    /// <summary>Herdr's socket API: one request line out, one reply line back, per connection.</summary>
    public static ReportUpstream UnixSocket(string herdrSocketPath) => async (line, cancellation) =>
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(herdrSocketPath), cancellation);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, cancellation);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        return await ReadLineAsync(reader, cancellation) ?? throw new IOException("herdr closed the connection without a reply");
    };

    /// <summary>
    /// Reads one line, stopping one character past <see cref="ReportRelay.MaxLineBytes"/> so a
    /// peer that never sends a newline cannot make the relay buffer without bound.
    /// </summary>
    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellation)
    {
        var builder = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellation);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            if (buffer[0] == '\n') return builder.ToString().TrimEnd('\r');
            builder.Append(buffer[0]);
            if (builder.Length > ReportRelay.MaxLineBytes) return builder.ToString();
        }
    }
}
