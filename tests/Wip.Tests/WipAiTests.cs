using Wip.Ai;
using Wip.Configuration;

namespace Wip.Tests;

public class WipAiTests
{
    [Fact]
    public void AnalyzerCollectsOnlyAllowListedFilesAndBoundsInput()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "README.md"), new string('r', ProjectAnalyzer.MaxFileCharacters + 100));
        File.WriteAllText(Path.Combine(directory.Path, ".env"), "SECRET=do-not-send");
        File.WriteAllText(Path.Combine(directory.Path, "random.txt"), "not relevant");

        var snapshot = new ProjectAnalyzer(directory.Path).Analyze();

        var file = Assert.Single(snapshot.Files);
        Assert.Equal("README.md", file.RelativePath);
        Assert.Contains("[truncated by wip]", file.Content);
        Assert.DoesNotContain("SECRET", snapshot.ToPromptText());
    }

    [Fact]
    public void GeneratorStripsFenceAndValidatesWithExistingParser()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "wip.yml");
        var provider = new StubProvider("""
            ```yaml
            version: 1
            mode: container
            container: app
            dependencies:
              app:
                image: ruby:3.4
            ```
            """);

        var result = new WipAiGenerator(provider).Generate(
            "Run Rails", new ProjectSnapshot(directory.Path, []), null, path);

        Assert.Equal("container", new ConfigLoader(path: Write(path, result)).Load().Mode);
        Assert.Contains("User request:\nRun Rails", provider.Prompt);
    }

    [Fact]
    public void ResolveBaseUrlPrefersExplicitArgumentThenEnvironmentThenDefault()
    {
        Assert.Equal("http://explicit", LocalAiProvider.ResolveBaseUrl("http://explicit"));

        using (new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, "http://from-env"))
        {
            Assert.Equal("http://from-env", LocalAiProvider.ResolveBaseUrl());
        }

        using (new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, null))
        {
            Assert.Equal(LocalAiProvider.DefaultBaseUrl, LocalAiProvider.ResolveBaseUrl());
        }
    }

    [Fact]
    public void ResolveModelPrefersExplicitArgumentThenEnvironmentThenNull()
    {
        Assert.Equal("explicit-model", LocalAiProvider.ResolveModel("explicit-model"));

        using (new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, "from-env-model"))
        {
            Assert.Equal("from-env-model", LocalAiProvider.ResolveModel());
        }

        using (new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, null))
        {
            Assert.Null(LocalAiProvider.ResolveModel());
        }
    }

    [Fact]
    public void IsAvailableFindsAListeningServerAndRejectsAClosedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(LocalAiProvider.IsAvailable($"http://127.0.0.1:{port}"));
        }
        finally
        {
            listener.Stop();
        }

        Assert.False(LocalAiProvider.IsAvailable("http://127.0.0.1:1"));
    }

    [Fact]
    public void GenerateSendsOpenAiCompatibleChatCompletionsRequest()
    {
        var handler = new StubHandler("""{"choices":[{"message":{"content":"version: 1"}}]}""");
        var provider = new LocalAiProvider("http://localhost:11434/v1", "llama3.1", handler);

        var result = provider.Generate("Run Rails", TestContext.Current.CancellationToken);

        Assert.Equal("version: 1", result);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.RequestUri?.ToString());
        Assert.Contains("\"model\":\"llama3.1\"", handler.RequestBody);
        Assert.Contains("Run Rails", handler.RequestBody);
    }

    [Fact]
    public void GenerateRejectsAMalformedResponse()
    {
        var handler = new StubHandler("""{"unexpected":true}""");
        var provider = new LocalAiProvider("http://localhost:11434/v1", "llama3.1", handler);

        Assert.Throws<WipException>(() => provider.Generate("Run Rails", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DiscoverModelReturnsTheOnlyChatCapableModel()
    {
        var handler = new StubHandler(
            """{"data":[{"id":"text-embedding-nomic-embed-text-v1.5"},{"id":"llama3.1"}]}""");

        var model = LocalAiProvider.DiscoverModel("http://localhost:11434/v1", handler);

        Assert.Equal("llama3.1", model);
        Assert.Equal("http://localhost:11434/v1/models", handler.RequestUri?.ToString());
    }

    [Fact]
    public void DiscoverModelThrowsWhenTheServerHasNoModelsLoaded()
    {
        var handler = new StubHandler("""{"data":[]}""");

        var exception = Assert.Throws<WipException>(() => LocalAiProvider.DiscoverModel("http://localhost:11434/v1", handler));
        Assert.Contains("No model configured", exception.Message);
    }

    [Fact]
    public void DiscoverModelExplainsALikelyMissingApiVersionPath()
    {
        var handler = new StubHandler("""{"error":"Unexpected endpoint or method. (GET /models)"}""");

        var exception = Assert.Throws<WipException>(() => LocalAiProvider.DiscoverModel("http://127.0.0.1:1234", handler));
        Assert.Contains("Unexpected endpoint or method", exception.Message);
        Assert.Contains("/v1", exception.Message);
    }

    [Fact]
    public void DiscoverModelThrowsListingChoicesWhenAmbiguous()
    {
        var handler = new StubHandler("""{"data":[{"id":"llama3.1"},{"id":"qwen2.5-coder"}]}""");

        var exception = Assert.Throws<WipException>(() => LocalAiProvider.DiscoverModel("http://localhost:11434/v1", handler));
        Assert.Contains("llama3.1", exception.Message);
        Assert.Contains("qwen2.5-coder", exception.Message);
    }

    [Fact]
    public void DiscoverModelRejectsANonArrayDataFieldInsteadOfCrashing()
    {
        var handler = new StubHandler("""{"data":{}}""");

        var exception = Assert.Throws<WipException>(() => LocalAiProvider.DiscoverModel("http://localhost:11434/v1", handler));
        Assert.Contains("not shaped as expected", exception.Message);
    }

    [Fact]
    public void DiscoverModelSkipsEntriesMissingAnIdInsteadOfCrashing()
    {
        var handler = new StubHandler("""{"data":[{}, {"id":"llama3.1"}]}""");

        var model = LocalAiProvider.DiscoverModel("http://localhost:11434/v1", handler);

        Assert.Equal("llama3.1", model);
    }

    [Fact]
    public void DiscoverModelWrapsATimeoutInsteadOfLettingItPropagateRaw()
    {
        var handler = new ThrowingHandler(new TaskCanceledException("The request timed out."));

        var exception = Assert.Throws<WipException>(() => LocalAiProvider.DiscoverModel("http://localhost:11434/v1", handler));
        Assert.Contains("did not respond in time", exception.Message);
    }

    [Fact]
    public void GenerateRejectsChoicesShapedAsSomethingOtherThanAnArray()
    {
        var handler = new StubHandler("""{"choices":"not-an-array"}""");
        var provider = new LocalAiProvider("http://localhost:11434/v1", "llama3.1", handler);

        var exception = Assert.Throws<WipException>(
            () => provider.Generate("Run Rails", TestContext.Current.CancellationToken));
        Assert.Contains("choices[0].message.content", exception.Message);
    }

    [Fact]
    public void GenerateRejectsAnEmptyChoicesArray()
    {
        var handler = new StubHandler("""{"choices":[]}""");
        var provider = new LocalAiProvider("http://localhost:11434/v1", "llama3.1", handler);

        Assert.Throws<WipException>(() => provider.Generate("Run Rails", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GenerateWrapsATimeoutInsteadOfLettingItPropagateRaw()
    {
        var handler = new ThrowingHandler(new TaskCanceledException("The request timed out."));
        var provider = new LocalAiProvider("http://localhost:11434/v1", "llama3.1", handler);

        var exception = Assert.Throws<WipException>(
            () => provider.Generate("Run Rails", TestContext.Current.CancellationToken));
        Assert.Contains("did not respond in time", exception.Message);
    }

    [Fact]
    public void GenerateLetsCallerRequestedCancellationPropagateUnwrapped()
    {
        var handler = new ThrowingHandler(new OperationCanceledException("cancelled by caller"));
        var provider = new LocalAiProvider("http://localhost:11434/v1", "llama3.1", handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => provider.Generate("Run Rails", cts.Token));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        internal Uri? RequestUri { get; private set; }
        internal string? RequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            });
        }
    }

    [Fact]
    public void GeneratorRejectsAContainerThatIsNotAString()
    {
        using var directory = new TemporaryDirectory();
        var generator = new WipAiGenerator(new StubProvider("""
            version: 1
            container:
              app:
                image: node:20
            """));

        var exception = Assert.Throws<ConfigException>(() => generator.Generate(
            "anything", new ProjectSnapshot(directory.Path, []), null, Path.Combine(directory.Path, "wip.yml")));
        Assert.Equal("container: must be a string", exception.Message);
    }

    [Fact]
    public void GeneratorRejectsAContainerThatDoesNotMatchADependency()
    {
        using var directory = new TemporaryDirectory();
        var generator = new WipAiGenerator(new StubProvider("""
            version: 1
            container: app
            dependencies:
              web:
                image: node:20
            """));

        var exception = Assert.Throws<ConfigException>(() => generator.Generate(
            "anything", new ProjectSnapshot(directory.Path, []), null, Path.Combine(directory.Path, "wip.yml")));
        Assert.Equal("No dependencies.app entry (check container: in wip.yml)", exception.Message);
    }

    [Fact]
    public void GeneratorRejectsInvalidCandidateBeforeItCanBeSaved()
    {
        using var directory = new TemporaryDirectory();
        var generator = new WipAiGenerator(new StubProvider("version: 999"));

        Assert.Throws<ConfigException>(() => generator.Generate(
            "anything", new ProjectSnapshot(directory.Path, []), null, Path.Combine(directory.Path, "wip.yml")));
    }

    private static string Write(string path, string content)
    {
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class StubProvider(string response) : IWipAiProvider
    {
        internal string? Prompt { get; private set; }
        public string Generate(string prompt, CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            return response;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-ai-test-").FullName;
        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>Sets an environment variable for the duration of a test and restores whatever
    /// value (if any) it had before, rather than assuming it started unset.</summary>
    private sealed class TemporaryEnvironmentVariable : IDisposable
    {
        private readonly string name;
        private readonly string? original;

        internal TemporaryEnvironmentVariable(string name, string? value)
        {
            this.name = name;
            original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(name, original);
    }
}
