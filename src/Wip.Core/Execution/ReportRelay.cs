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
/// The sandbox never sees Herdr's pipe. It writes report lines into a FIFO at
/// <see cref="FifoPath"/> inside the sandbox, which the host relay reads by running
/// <c>cat</c> on it through <c>wip sandbox exec</c>. The channel is one-way: the sandbox
/// gets no reply. The relay forwards exactly the two report methods in
/// <see cref="AllowedMethods"/>; everything else -- reading panes, sending input, creating
/// workspaces -- is refused, so a sandboxed agent cannot drive the rest of the Herdr API.
/// </para>
/// <para>
/// The relay stamps <c>params.source</c> and <c>params.pane_id</c> itself, from the sandbox
/// name and the relay's own <c>HERDR_PANE_ID</c>. A line that names a pane is refused, so a
/// sandbox cannot report about another pane.
/// </para>
/// </remarks>
public static class ReportRelay
{
    /// <summary>Fixed in-sandbox directory holding the report FIFO.</summary>
    public const string FifoDirectory = "/run/wip";

    /// <summary>Fixed in-sandbox path of the report FIFO.</summary>
    public const string FifoPath = FifoDirectory + "/report.fifo";

    public const string FifoVariable = "WIP_REPORT_FIFO";
    public const string SourceVariable = "WIP_REPORT_SOURCE";
    public const string PaneVariable = "HERDR_PANE_ID";

    /// <summary>Host variables handed through unchanged when present.</summary>
    public static readonly IReadOnlyList<string> PassThroughVariables = [PaneVariable];

    public static readonly IReadOnlySet<string> AllowedMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "pane.report_agent",
        "pane.report_agent_session",
    };

    /// <summary>Longest accepted line; reports are small, so anything bigger is refused.</summary>
    public const int MaxLineBytes = 16 * 1024;

    /// <summary>
    /// Creates the FIFO idempotently, owned by the sandbox exec user -- the user the CLI runs
    /// as, since neither passes a user -- with mode 0600 in a 0700 directory of that owner.
    /// An existing FIFO is reused only with exactly that owner and mode; a symlink, a
    /// non-FIFO, or a FIFO or directory with another owner or mode is refused, never fixed
    /// up. Fixed text: no value from the config or the host is interpolated into the shell.
    /// </summary>
    public static readonly string CreateFifoScript = string.Join(" ; ",
        "umask 077",
        "u=$(id -u)",
        "if [ -L @DIR@ ]; then echo '@DIR@ is a symlink' >&2; exit 1; fi",
        "mkdir -p @DIR@ || exit 1",
        "if [ \"$(stat -c %u @DIR@)\" != \"$u\" ]; then echo \"@DIR@ is not owned by uid $u\" >&2; exit 1; fi",
        "chmod 700 @DIR@ || exit 1",
        "if [ -L @FIFO@ ] || { [ -e @FIFO@ ] && [ ! -p @FIFO@ ]; }; then echo '@FIFO@ exists and is not a FIFO' >&2; exit 1; fi",
        "if [ -p @FIFO@ ]; then m=$(stat -c '%u %a' @FIFO@); if [ \"$m\" != \"$u 600\" ]; then echo \"@FIFO@ has owner/mode $m, expected $u 600\" >&2; exit 1; fi; " +
            "else mkfifo -m 600 @FIFO@ && chown \"$u:$(id -g)\" @FIFO@ || exit 1; fi")
        .Replace("@FIFO@", FifoPath, StringComparison.Ordinal)
        .Replace("@DIR@", FifoDirectory, StringComparison.Ordinal);

    /// <summary>Reads the FIFO until its writers close it; fails if it is not a FIFO or is a symlink.</summary>
    public static readonly IReadOnlyList<string> ReadFifoCommand = ["sh", "-c", "[ -p " + FifoPath + " ] && [ ! -L " + FifoPath + " ] && exec cat " + FifoPath];

    public static string SourceId(string sandbox) => "wip:" + sandbox;

    /// <summary>Host-side lock for a sandbox's relay, beside the config under <c>.wip</c>.</summary>
    public static string LockPath(string configDirectory, string sandbox) =>
        Path.Combine(configDirectory, ".wip", "report-relay", sandbox + ".lock");

    /// <summary>
    /// Takes the exclusive per-sandbox relay lock (<c>flock</c> on Unix, a share-none handle
    /// on Windows), held by the caller for the relay's whole life. A FIFO write reaches only
    /// one reader, so a second relay would split reports between them -- and stamp them with
    /// its own pane -- which is why it refuses to start instead.
    /// </summary>
    public static FileStream AcquireRelayLock(string lockPath, string sandbox)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            return new FileStream(lockPath, options);
        }
        catch (IOException e)
        {
            throw new WipException($"Sandbox {sandbox}: a report relay is already running ({lockPath} is locked); stop it before starting another", e);
        }
    }

    /// <summary>
    /// Variables an exec into a relay-enabled sandbox exports: the FIFO path, the source id,
    /// and the pass-through variables the host actually has. Values with control characters
    /// are dropped rather than passed, since they cannot be a pane id.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ExecEnvironment(string sandbox, Func<string, string?> host)
    {
        var result = new List<KeyValuePair<string, string>>
        {
            new(FifoVariable, FifoPath),
            new(SourceVariable, SourceId(sandbox)),
        };
        foreach (var name in PassThroughVariables)
        {
            if (host(name) is { Length: > 0 } value && !value.Any(char.IsControl)) result.Add(new(name, value));
        }
        return result;
    }

    /// <summary>Checks one line from the sandbox and, when allowed, rewrites it for Herdr.</summary>
    public static ReportRelayDecision Filter(string line, string source, string paneId)
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
            if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("pane_id", out _))
                return Refuse(id, "pane_id is set by the relay, not the sandbox");

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
                writer.WriteString("pane_id", paneId);
                writer.WriteString("source", source);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return new(true, Encoding.UTF8.GetString(buffer.ToArray()), id, null);
        }
    }

    private static ReportRelayDecision Refuse(string? id, string error) => new(false, null, id, error);

    /// <summary>
    /// Hands each line of <paramref name="reader"/> to <paramref name="onLine"/> until EOF.
    /// A line longer than <see cref="MaxLineBytes"/> is passed on cut short, so the filter
    /// refuses it, and the rest of it is skipped without being buffered.
    /// </summary>
    public static async Task PumpLinesAsync(TextReader reader, Func<string, Task> onLine, CancellationToken cancellation)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var discarding = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellation);
            if (read == 0)
            {
                if (builder.Length > 0 && !discarding) await onLine(builder.ToString());
                return;
            }
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    if (!discarding) await onLine(builder.ToString().TrimEnd('\r'));
                    builder.Clear();
                    discarding = false;
                }
                else if (!discarding)
                {
                    builder.Append(c);
                    if (builder.Length > MaxLineBytes)
                    {
                        await onLine(builder.ToString());
                        builder.Clear();
                        discarding = true;
                    }
                }
            }
        }
    }
}

/// <summary>Forwards one accepted line to Herdr and returns Herdr's one-line reply.</summary>
public delegate Task<string> ReportUpstream(string line, CancellationToken cancellation);

/// <summary>
/// Runs one read of the sandbox FIFO, handing each line to the callback, and returns the
/// reader's exit code once the FIFO's writers have all closed it.
/// </summary>
public delegate Task<int> ReportFifoReader(Func<string, Task> onLine, CancellationToken cancellation);

/// <summary>
/// Filters each line from the sandbox and forwards accepted ones to Herdr. The sandbox gets
/// no reply over a FIFO, so refusals and Herdr's errors go to <paramref name="log"/>.
/// </summary>
public sealed class ReportRelayServer(string sandbox, string paneId, ReportUpstream upstream, Action<string> log)
{
    /// <summary>How long Herdr gets to accept, read and answer one report.</summary>
    public static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(5);

    private readonly string _source = ReportRelay.SourceId(sandbox);

    /// <summary>Handles one line; a bad line or an unreachable Herdr is logged, never thrown.</summary>
    public async Task HandleLineAsync(string line, CancellationToken cancellation)
    {
        if (line.Trim().Length == 0) return;
        var decision = ReportRelay.Filter(line, _source, paneId);
        if (!decision.Accepted)
        {
            log($"report relay {sandbox}: refused {decision.Id ?? "-"}: {decision.Error}");
            return;
        }
        string reply;
        try
        {
            reply = await upstream(decision.Forward!, cancellation);
        }
        catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException)
        {
            log($"report relay {sandbox}: herdr is unreachable: {e.Message}");
            return;
        }
        if (!reply.Contains("\"result\"", StringComparison.Ordinal)) log($"report relay {sandbox}: herdr answered {reply}");
    }

    /// <summary>
    /// Reads the FIFO until cancelled. A read that ends cleanly -- every writer closed the
    /// FIFO, so <c>cat</c> saw EOF -- is reopened at once.
    /// </summary>
    /// <remarks>
    /// Only the first <paramref name="ensureFifo"/> fails fast, so a misconfiguration is
    /// reported at once. After that, a failed read or a <see cref="WipException"/> -- the
    /// sandbox stopped, a status probe failed -- is retried after a backoff that starts at
    /// <paramref name="retryDelay"/> and doubles up to <paramref name="maxRetryDelay"/>,
    /// re-creating the FIFO first, so a restarted or recreated sandbox gets it back. A clean
    /// read resets the backoff.
    /// </remarks>
    public async Task RunAsync(ReportFifoReader reader, Func<CancellationToken, Task> ensureFifo, TimeSpan retryDelay,
        CancellationToken cancellation, TimeSpan? maxRetryDelay = null)
    {
        var ceiling = maxRetryDelay ?? TimeSpan.FromSeconds(30);
        try
        {
            await ensureFifo(cancellation);
            var delay = retryDelay;
            var recreate = false;
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (recreate) await ensureFifo(cancellation);
                    recreate = false;
                    var code = await reader(line => HandleLineAsync(line, cancellation), cancellation);
                    if (code == 0 || cancellation.IsCancellationRequested)
                    {
                        delay = retryDelay;
                        continue;
                    }
                    log($"report relay {sandbox}: FIFO reader exited {code}; retrying in {delay.TotalSeconds:0.###}s");
                }
                catch (Exception e) when (e is WipException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException
                    && !cancellation.IsCancellationRequested)
                {
                    log($"report relay {sandbox}: {e.Message}; retrying in {delay.TotalSeconds:0.###}s");
                }
                recreate = true;
                await Task.Delay(delay, cancellation);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, ceiling.Ticks));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
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
    /// costs one timeout rather than holding up every later report. The timeout surfaces as
    /// an <see cref="IOException"/>, which the relay logs as unreachable.
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
