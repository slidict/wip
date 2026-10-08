using System.IO.Pipes;
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
/// report under another's name. Every other param, <c>pane_id</c> included, passes through
/// for Herdr to validate.
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
        "pane.report_agent",
        "pane.report_agent_session",
    };

    /// <summary>Longest accepted line; reports are small, so anything bigger is refused.</summary>
    public const int MaxLineBytes = 16 * 1024;

    /// <summary>Host directory bound into the sandbox, kept beside the config under <c>.wip</c>.</summary>
    public static string HostDirectory(string configDirectory, string sandbox) =>
        Path.Combine(configDirectory, ".wip", "report-relay", sandbox);

    public static string SourceId(string sandbox) => "wip:" + sandbox;

    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates the relay directory and sets it to 0700 explicitly, whatever the umask, so no
    /// other local user can reach the socket inside it. On Windows the directory ACL applies.
    /// </summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }
        Directory.CreateDirectory(directory, OwnerOnlyDirectory);
        // CreateDirectory's mode is masked by the umask and skipped for an existing directory.
        File.SetUnixFileMode(directory, OwnerOnlyDirectory);
    }

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
public sealed class ReportRelayServer(string sandbox, ReportUpstream upstream)
{
    /// <summary>How long Herdr gets to accept, read and answer one report.</summary>
    public static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(5);

    private readonly string _source = ReportRelay.SourceId(sandbox);

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
            var decision = ReportRelay.Filter(line, _source);
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
                catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException)
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
        ReportRelay.EnsurePrivateDirectory(Path.GetDirectoryName(Path.GetFullPath(socketPath))!);
        // Held for the relay's whole life: only the holder may treat the socket as its own.
        using var instance = AcquireInstanceLock(socketPath);
        // The lock is ours, so a socket still at the path was left by a relay that is gone.
        if (File.Exists(socketPath)) File.Delete(socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        try
        {
            // Bind's mode is 0777 minus the umask; set it rather than trust that.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socketPath, ReportRelay.OwnerOnlyFile);
            listener.Listen(16);
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
            // Still under the instance lock, so this socket is the one this relay bound.
            try { File.Delete(socketPath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// An exclusive lock beside the socket (<c>flock</c> on Unix, a share-none handle on
    /// Windows), so a second relay for the same sandbox refuses to start instead of
    /// replacing the live socket under the first.
    /// </summary>
    private FileStream AcquireInstanceLock(string socketPath)
    {
        var lockPath = socketPath + ".lock";
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = ReportRelay.OwnerOnlyFile;
        try
        {
            return new FileStream(lockPath, options);
        }
        catch (IOException e)
        {
            throw new WipException($"Sandbox {sandbox}: a report relay is already running ({lockPath} is locked); stop it before starting another", e);
        }
    }

    /// <summary>The transport Herdr listens on for this host.</summary>
    public static ReportUpstream ForHost(string herdrSocketPath) =>
        OperatingSystem.IsWindows() ? NamedPipe(herdrSocketPath) : UnixSocket(herdrSocketPath);

    /// <summary>
    /// Herdr on Windows: <c>HERDR_SOCKET_PATH</c> names a regular file (pid:nonce), not a
    /// socket, and its literal value is the name of the named pipe Herdr serves.
    /// </summary>
    public static ReportUpstream NamedPipe(string herdrSocketPath, TimeSpan? timeout = null) => (line, cancellation) =>
        WithDeadlineAsync(timeout ?? UpstreamTimeout, cancellation, async deadline =>
        {
            await using var pipe = new NamedPipeClientStream(".", herdrSocketPath, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline);
            return await ExchangeAsync(pipe, line, deadline);
        });

    /// <summary>Herdr on Linux and macOS: an AF_UNIX socket at <c>HERDR_SOCKET_PATH</c>.</summary>
    public static ReportUpstream UnixSocket(string herdrSocketPath, TimeSpan? timeout = null) => (line, cancellation) =>
        WithDeadlineAsync(timeout ?? UpstreamTimeout, cancellation, async deadline =>
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(herdrSocketPath), deadline);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            return await ExchangeAsync(stream, line, deadline);
        });

    /// <summary>
    /// Bounds connect, write and read together, so a Herdr that accepts but never answers
    /// costs one timeout rather than a connection held until the relay stops. The timeout
    /// surfaces as an <see cref="IOException"/>, which the relay reports as unreachable.
    /// </summary>
    private static async Task<string> WithDeadlineAsync(TimeSpan timeout, CancellationToken cancellation, Func<CancellationToken, Task<string>> exchange)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try
        {
            return await exchange(deadline.Token);
        }
        catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException($"herdr did not answer within {timeout.TotalSeconds:0.###} seconds", e);
        }
    }

    /// <summary>Herdr's API: one request line out, one reply line back, per connection.</summary>
    private static async Task<string> ExchangeAsync(Stream stream, string line, CancellationToken cancellation)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellation);
        await stream.FlushAsync(cancellation);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        return await ReadLineAsync(reader, cancellation) ?? throw new IOException("herdr closed the connection without a reply");
    }

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
