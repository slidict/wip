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
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    private readonly string baseUrl;
    private readonly string model;
    private readonly HttpMessageHandler? handler;

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
    public static string DiscoverModel(string baseUrl, HttpMessageHandler? handler = null)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(5);

        HttpResponseMessage response;
        try
        {
            response = client.GetAsync($"{baseUrl}/models").GetAwaiter().GetResult();
        }
        catch (HttpRequestException exception)
        {
            throw new WipException(NotFoundMessage(baseUrl), exception);
        }
        catch (OperationCanceledException exception)
        {
            // DiscoverModel takes no caller cancellationToken, so any cancellation here can
            // only be the client.Timeout above firing.
            throw new WipException($"Local AI server at '{baseUrl}' did not respond in time", exception);
        }

        using (response)
        {
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new WipException($"Local AI server returned {(int)response.StatusCode} listing models: {text}");
            }

            var models = ExtractModelIds(text);
            return models.Count switch
            {
                0 => throw new WipException(MissingModelMessage()),
                1 => models[0],
                _ => throw new WipException(AmbiguousModelMessage(models)),
            };
        }
    }

    private static IReadOnlyList<string> ExtractModelIds(string responseBody)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException exception)
        {
            throw new WipException("Local AI server returned a models list wip could not parse", exception);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                var detail = document.RootElement.TryGetProperty("error", out var error)
                    ? error.ToString()
                    : responseBody;
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
    }

    public string Generate(string prompt, CancellationToken cancellationToken = default)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(5);

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
                },
                cancellationToken).GetAwaiter().GetResult();
        }
        catch (HttpRequestException exception)
        {
            throw new WipException(NotFoundMessage(baseUrl), exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's own token is still live, so this cancellation can only be
            // client.Timeout firing — a genuine caller cancellation instead propagates as-is.
            throw new WipException($"Local AI server at '{baseUrl}' did not respond in time", exception);
        }

        using (response)
        {
            var text = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new WipException($"Local AI server returned {(int)response.StatusCode}: {text}");
            }

            return ExtractContent(text);
        }
    }

    private static string ExtractContent(string responseBody)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException exception)
        {
            throw new WipException("Local AI server returned a response wip could not parse", exception);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.String)
            {
                throw new WipException(
                    "Local AI server response did not contain the expected choices[0].message.content field");
            }

            var content = contentElement.GetString();
            return string.IsNullOrWhiteSpace(content)
                ? throw new WipException("Local AI server returned an empty response")
                : content;
        }
    }
}
