using Wip.Ai;
using Wip.Configuration;
using Wip.Diagnostics;
using Wip.Platform;

namespace Wip.Tests;

[Collection(AiEnvironmentVariableCollection.Name)]
public class DoctorTests
{
    [Fact]
    public void ReportsEnglishDisplayLanguageWithNoQualifier()
    {
        using var directory = new TemporaryDirectory();
        var results = DoctorFor(directory, aiAvailable: _ => false)
            .Call(englishDisplayLanguage: true);

        var language = Assert.Single(results, result => result.Message.StartsWith("Display language:"));
        Assert.Equal(Doctor.Level.Ok, language.Level);
        Assert.Equal("Display language: English", language.Message);
    }

    [Fact]
    public void FlagsANonEnglishDisplayLanguageAsPossiblyAffectingToolOutput()
    {
        using var directory = new TemporaryDirectory();
        var results = DoctorFor(directory, aiAvailable: _ => false)
            .Call(englishDisplayLanguage: false);

        var language = Assert.Single(results, result => result.Message.StartsWith("Display language:"));
        Assert.Equal(Doctor.Level.Ok, language.Level);
        Assert.Contains("not English", language.Message);
        Assert.Contains("may appear in a different language", language.Message);
    }

    [Fact]
    public void MissingWslcSuggestsStableWslUpdateAndVersionCheck()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "wip.yml"), """
            version: 1
            container: app
            dependencies:
              app:
                image: busybox:latest
            """);
        var resolver = new Wip.Execution.CommandResolver([]);

        // This test is about the wslc hint; reporting the AI server as absent keeps the AI
        // check out of it without depending on nothing listening on a real port.
        var results = new Doctor(
            new ConfigLoader(directory.Path), new FakeEnvironment(), resolver,
            aiAvailable: _ => false).Call();

        var wslc = Assert.Single(results, result => result.Message.StartsWith("WSLC was not found."));
        Assert.Equal(Doctor.Level.Fail, wslc.Level);
        var message = wslc.Message.ReplaceLineEndings("\n");
        Assert.Contains("\n  wsl --update\n", message);
        Assert.Contains("\n  wsl --update --web-download\n", message);
        Assert.Contains("\n  wsl --version\n", message);
        Assert.Contains("WSL 3.0.1", message);
        Assert.DoesNotContain("--pre-release", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportsMissingAiServerAsWarnWithFixHint()
    {
        using var baseUrl = new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, "http://127.0.0.1:1");
        using var directory = new TemporaryDirectory();

        var results = DoctorFor(directory, aiAvailable: _ => false).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("local AI server"));
        Assert.Equal(Doctor.Level.Warn, ai.Level);
        Assert.Contains("wip init --ai", ai.Message);
        Assert.Contains(LocalAiProvider.BaseUrlEnvironmentVariable, ai.Message);
        Assert.Contains("http://127.0.0.1:1", ai.Message);
    }

    [Fact]
    public void CallArgumentOverridesTheBaseUrlEnvironmentVariable()
    {
        using var baseUrl = new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, "http://127.0.0.1:1");
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, "llama3.1");
        using var directory = new TemporaryDirectory();
        // The point of the test is which URL reaches the probe, so record it rather than
        // standing up a server on one of the two.
        var probed = new List<string>();

        var results = DoctorFor(directory, aiAvailable: url =>
        {
            probed.Add(url);
            return true;
        }).Call("http://127.0.0.1:4242");

        var ai = Assert.Single(results, result => result.Message.Contains("Local AI server"));
        Assert.Equal(Doctor.Level.Ok, ai.Level);
        Assert.Equal("http://127.0.0.1:4242", Assert.Single(probed));
        Assert.Contains("http://127.0.0.1:4242", ai.Message);
    }

    [Fact]
    public void ReportsMissingModelAsWarnWithFixHintWhenServerHasNoneLoaded()
    {
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, null);
        using var directory = new TemporaryDirectory();

        var results = DoctorFor(directory, Models("""{"data":[]}""")).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("No model configured"));
        Assert.Equal(Doctor.Level.Warn, ai.Level);
        Assert.Contains(LocalAiProvider.ModelEnvironmentVariable, ai.Message);
    }

    [Fact]
    public void AutoDiscoversTheOnlyModelTheServerHasLoaded()
    {
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, null);
        using var directory = new TemporaryDirectory();

        var results = DoctorFor(directory, Models("""{"data":[{"id":"llama3.1"}]}""")).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("Local AI server"));
        Assert.Equal(Doctor.Level.Ok, ai.Level);
        Assert.Contains("llama3.1", ai.Message);
    }

    [Fact]
    public void ReportsAmbiguousModelsAsWarnListingTheChoices()
    {
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, null);
        using var directory = new TemporaryDirectory();

        var results = DoctorFor(
            directory,
            Models("""{"data":[{"id":"llama3.1"},{"id":"qwen2.5-coder"}]}""")).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("more than one loaded"));
        Assert.Equal(Doctor.Level.Warn, ai.Level);
        Assert.Contains("llama3.1", ai.Message);
        Assert.Contains("qwen2.5-coder", ai.Message);
    }

    [Fact]
    public void ReportsAnAvailableAiServerWithModelAsOk()
    {
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, "llama3.1");
        using var directory = new TemporaryDirectory();

        // A configured model short-circuits discovery, so availability is the only AI input.
        var results = DoctorFor(directory, aiAvailable: _ => true).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("Local AI server"));
        Assert.Equal(Doctor.Level.Ok, ai.Level);
    }

    /// <summary>
    /// A doctor whose AI checks answer from memory instead of the network.
    /// <see cref="LocalAiProvider.IsAvailable"/> opens a real TCP connection and
    /// <see cref="LocalAiProvider.DiscoverModel"/> issues a real HTTP request, so testing
    /// through them meant binding a port and hoping nothing else claimed it — which is
    /// exactly what failed on CI (slidict/workspace#276).
    /// </summary>
    private static Doctor DoctorFor(
        TemporaryDirectory directory,
        Func<string, string>? aiDiscoverModel = null,
        Func<string, bool>? aiAvailable = null) =>
        new(new ConfigLoader(directory.Path),
            new FakeEnvironment(),
            aiAvailable: aiAvailable ?? (_ => true),
            aiDiscoverModel: aiDiscoverModel);

    /// <summary>
    /// Discovery against a canned <c>/models</c> body. The real
    /// <see cref="LocalAiProvider.DiscoverModel"/> still decides what the list means, so the
    /// "no model" / "one model" / "ambiguous" behaviour under test is the production logic —
    /// only the transport is stubbed.
    /// </summary>
    private static Func<string, string> Models(string responseBody) =>
        baseUrl => LocalAiProvider.DiscoverModel(baseUrl, new StubHandler(responseBody));

    private sealed class FakeEnvironment : IEnvironment
    {
        public bool IsInteractive => false;
        public bool IsWsl2 => true;
        public string Architecture => "linux/amd64";
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-doctor-test-").FullName;
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
