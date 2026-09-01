using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wip.Ai;

/// <summary>
/// Talks to a local OpenAI-compatible chat completions endpoint, such as Ollama
/// (<c>ollama serve</c>) or LM Studio's local server. Both already speak the same
/// <c>/chat/completions</c> shape, so one provider covers either without wip taking a
/// dependency on a specific vendor's native protocol.
/// </summary>
public sealed class LocalAiProvider : IWipAiProvider
{
    public const string BaseUrlEnvironmentVariable = "WIP_AI_BASE_URL";
    public const string ModelEnvironmentVariable = "WIP_AI_MODEL";
    public const string DefaultBaseUrl = "http://localhost:11434/v1";
    internal const long ModelsResponseMaxBytes = 2 * 1024 * 1024;
    internal const long ChatResponseMaxBytes = 4 * 1024 * 1024;
    internal const int GeneratedContentMaxCharacters = 1024 * 1024;
    internal const int ErrorBodyMaxBytes = 4 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(5);

    private readonly string baseUrl;
    private readonly string model;
    private readonly HttpMessageHandler? handler;

    /// <summary>How long a whole <see cref="Generate"/> call may take, headers and body
    /// together. Overridden only by tests, which cannot wait out the real deadline.</summary>
    internal TimeSpan RequestTimeout { get; init; } = GenerateTimeout;

    public LocalAiProvider(string baseUrl, string model, HttpMessageHandler? handler = null)
    {
        this.baseUrl = baseUrl;
        this.model = model;
        this.handler = handler;
    }

    /// <summary>The base URL a default instance would use: an explicit value, then the
    /// environment variable, then Ollama's default.</summary>
    public static string ResolveBaseUrl(string? baseUrl = null) => (baseUrl
        ?? Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable)
        ?? DefaultBaseUrl).TrimEnd('/');

    /// <summary>The model name a default instance would use, or <c>null</c> if none is
    /// configured — there is no sensible default model to guess.</summary>
    public static string? ResolveModel(string? model = null) => model
        ?? Environment.GetEnvironmentVariable(ModelEnvironmentVariable);

    /// <summary>
    /// Whether a server is listening at <paramref name="baseUrl"/>, so <c>wip doctor</c> and
    /// <c>wip init --ai</c> can report a missing server up front instead of only after the
    /// user has typed a whole request into a prompt that was never going anywhere.
    /// </summary>
    public static bool IsAvailable(string? baseUrl = null)
    {
        Uri uri;
        try
        {
            uri = new Uri(ResolveBaseUrl(baseUrl));
        }
        catch (UriFormatException)
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(uri.Host, uri.Port).Wait(ProbeTimeout);
        }
        catch (Exception exception) when (exception is AggregateException or ArgumentException)
        {
            // ArgumentException covers a URI with no host or no default port for its scheme
            // (e.g. file:///tmp), which TcpClient rejects synchronously before ConnectAsync
            // ever returns a Task — too early for the AggregateException catch above to see it.
            return false;
        }
    }

    public static string NotFoundMessage(string baseUrl) =>
        $"No local AI server found at '{baseUrl}'. Start Ollama (`ollama serve`) or LM Studio's " +
        $"local server, or set {BaseUrlEnvironmentVariable} to another OpenAI-compatible server's URL.";

    public static string MissingModelMessage() =>
        $"No model configured, and the server has no model loaded either. Set {ModelEnvironmentVariable} " +
        "to a model name already pulled or loaded in your local AI server, e.g. WIP_AI_MODEL=llama3.1.";

    public static string AmbiguousModelMessage(IReadOnlyList<string> models) =>
        $"No model configured, and the server has more than one loaded: {string.Join(", ", models)}. " +
        $"Set {ModelEnvironmentVariable} to the one to use, e.g. {ModelEnvironmentVariable}={models[0]}.";

    /// <summary>
    /// Asks the server which models it has via its OpenAI-compatible <c>/models</c> endpoint, so
    /// wip can skip asking for <see cref="ModelEnvironmentVariable"/> when there is only one
    /// reasonable answer — the common case for a single `ollama pull`. Embedding models are
    /// excluded since they cannot generate the chat completion wip needs.
    /// </summary>
    public static string DiscoverModel(string baseUrl, HttpMessageHandler? handler = null) =>
        DiscoverModel(baseUrl, handler, DiscoveryTimeout);

    internal static string DiscoverModel(string baseUrl, HttpMessageHandler? handler, TimeSpan timeout)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = timeout;
        // ResponseHeadersRead stops client.Timeout once the headers are in, so the body reads
        // below need a deadline of their own or a server that stalls mid-body hangs wip forever.
        // One source started before the request gives headers and body a single budget.
        using var deadline = new CancellationTokenSource(timeout);

        HttpResponseMessage response;
        try
        {
            response = client.GetAsync(
                $"{baseUrl}/models", HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .GetAwaiter().GetResult();
        }
        catch (HttpRequestException exception)
        {
            throw new WipException(NotFoundMessage(baseUrl), exception);
        }
        catch (OperationCanceledException exception)
        {
            // DiscoverModel takes no caller cancellationToken, so any cancellation here can
            // only be the deadline above firing.
            throw new WipException($"Local AI server at '{baseUrl}' did not respond in time", exception);
        }

        using (response)
        {
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = ReadErrorSnippetAsync(response.Content, deadline.Token).GetAwaiter().GetResult();
                    throw new WipException(
                        $"Local AI server returned {(int)response.StatusCode} listing models: {detail}");
                }

                using var document = ParseResponseAsync(
                    response.Content, ModelsResponseMaxBytes, "models list", deadline.Token).GetAwaiter().GetResult();
                var models = ExtractModelIds(document);
                return models.Count switch
                {
                    0 => throw new WipException(MissingModelMessage()),
                    1 => models[0],
                    _ => throw new WipException(AmbiguousModelMessage(models)),
                };
            }
            catch (OperationCanceledException exception)
            {
                throw new WipException($"Local AI server at '{baseUrl}' did not respond in time", exception);
            }
        }
    }

    private static IReadOnlyList<string> ExtractModelIds(JsonDocument document)
    {
        // TryGetProperty throws on anything but an object, so a bare array or string body would
        // escape the WipException contract Doctor.CheckAi relies on.
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new WipException(
                "Local AI server's models list was not shaped as expected: response is not an object");
        }

        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            var detail = document.RootElement.TryGetProperty("error", out var error)
                ? TruncateAndEscape(error.ToString(), ErrorBodyMaxBytes)
                : "unexpected response";
            throw new WipException(
                $"Local AI server rejected the models request: {detail} — check that " +
                $"{BaseUrlEnvironmentVariable}/--url includes the API version path, e.g. '.../v1'.");
        }

        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new WipException("Local AI server's models list was not shaped as expected: data is not an array");
        }

        var models = new List<string>();
        foreach (var element in data.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String &&
                idElement.GetString() is { Length: > 0 } id &&
                !id.Contains("embed", StringComparison.OrdinalIgnoreCase))
            {
                models.Add(id);
            }
        }

        return models;
    }

    public string Generate(string prompt, CancellationToken cancellationToken = default)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = RequestTimeout;
        // As in DiscoverModel: ResponseHeadersRead retires client.Timeout at the headers, so the
        // body reads below run under a linked source that keeps both the caller's cancellation
        // and a deadline covering the whole exchange.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["stream"] = false,
        };

        HttpResponseMessage response;
        try
        {
            response = client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                }, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).GetAwaiter().GetResult();
        }
        catch (HttpRequestException exception)
        {
            throw new WipException(NotFoundMessage(baseUrl), exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's own token is still live, so this cancellation can only be the
            // deadline firing — a genuine caller cancellation instead propagates as-is.
            throw new WipException($"Local AI server at '{baseUrl}' did not respond in time", exception);
        }

        using (response)
        {
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = ReadErrorSnippetAsync(response.Content, deadline.Token).GetAwaiter().GetResult();
                    throw new WipException($"Local AI server returned {(int)response.StatusCode}: {detail}");
                }

                using var document = ParseResponseAsync(
                    response.Content, ChatResponseMaxBytes, "response", deadline.Token).GetAwaiter().GetResult();
                return ExtractContent(document);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WipException($"Local AI server at '{baseUrl}' did not respond in time", exception);
            }
        }
    }

    private static string ExtractContent(JsonDocument document)
    {
        // Every TryGetProperty below is guarded by a ValueKind check, since TryGetProperty throws
        // InvalidOperationException — not a WipException — on a non-object element.
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var contentElement) ||
            contentElement.ValueKind != JsonValueKind.String)
        {
            throw new WipException(
                "Local AI server response did not contain the expected choices[0].message.content field");
        }

        var content = contentElement.GetString();
        if (content?.Length > GeneratedContentMaxCharacters)
        {
            throw new WipException(
                $"Local AI server generated content larger than the {GeneratedContentMaxCharacters}-character limit");
        }

        return string.IsNullOrWhiteSpace(content)
            ? throw new WipException("Local AI server returned an empty response")
            : content;
    }

    private static async Task<JsonDocument> ParseResponseAsync(
        HttpContent content, long maxBytes, string description, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new WipException(
                $"Local AI server returned a {description} larger than the {maxBytes}-byte limit");
        }

        try
        {
            await using var responseStream = await content.ReadAsStreamAsync(cancellationToken);
            await using var limitedStream = new LimitedReadStream(responseStream, maxBytes);
            return await JsonDocument.ParseAsync(limitedStream, cancellationToken: cancellationToken);
        }
        catch (ResponseTooLargeException exception)
        {
            throw new WipException(
                $"Local AI server returned a {description} larger than the {maxBytes}-byte limit", exception);
        }
        catch (JsonException exception)
        {
            throw new WipException($"Local AI server returned a {description} wip could not parse", exception);
        }
    }

    private static async Task<string> ReadErrorSnippetAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[ErrorBodyMaxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
            if (count == 0) break;
            read += count;
        }

        var truncated = read > ErrorBodyMaxBytes;
        var text = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, ErrorBodyMaxBytes));
        return TruncateAndEscape(text, ErrorBodyMaxBytes) + (truncated ? "… [truncated]" : string.Empty);
    }

    private static string TruncateAndEscape(string value, int maxCharacters)
    {
        var length = Math.Min(value.Length, maxCharacters);
        var result = new StringBuilder(length);
        for (var index = 0; index < length; index++)
        {
            var character = value[index];
            result.Append(char.IsControl(character) ? $"\\u{(int)character:x4}" : character);
        }
        return result.ToString();
    }

    private sealed class ResponseTooLargeException : IOException
    {
    }

    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => bytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            var remainingWithSentinel = maxBytes - bytesRead + 1;
            if (remainingWithSentinel <= 0) throw new ResponseTooLargeException();
            var count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remainingWithSentinel)], cancellationToken);
            bytesRead += count;
            if (bytesRead > maxBytes) throw new ResponseTooLargeException();
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
