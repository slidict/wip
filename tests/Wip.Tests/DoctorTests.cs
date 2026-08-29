using Wip.Ai;
using Wip.Configuration;
using Wip.Diagnostics;
using Wip.Platform;

namespace Wip.Tests;

[Collection(AiEnvironmentVariableCollection.Name)]
public class DoctorTests
{
    [Fact]
    public void ReportsMissingAiServerAsWarnWithFixHint()
    {
        using var baseUrl = new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, "http://127.0.0.1:1");
        using var directory = new TemporaryDirectory();
        var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("local AI server"));
        Assert.Equal(Doctor.Level.Warn, ai.Level);
        Assert.Contains("wip init --ai", ai.Message);
        Assert.Contains(LocalAiProvider.BaseUrlEnvironmentVariable, ai.Message);
    }

    [Fact]
    public void CallArgumentOverridesTheBaseUrlEnvironmentVariable()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var baseUrl = new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, "http://127.0.0.1:1");
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, "llama3.1");
        using var directory = new TemporaryDirectory();

        var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment())
            .Call($"http://127.0.0.1:{port}");

        var ai = Assert.Single(results, result => result.Message.Contains("Local AI server"));
        Assert.Equal(Doctor.Level.Ok, ai.Level);
    }

    [Fact]
    public void ReportsMissingModelAsWarnWithFixHintWhenServerHasNoneLoaded()
    {
        using var server = new FakeModelsServer("""{"data":[]}""");
        using var baseUrl = new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, server.BaseUrl);
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, null);
        using var directory = new TemporaryDirectory();

        var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("No model configured"));
        Assert.Equal(Doctor.Level.Warn, ai.Level);
        Assert.Contains(LocalAiProvider.ModelEnvironmentVariable, ai.Message);
    }

    [Fact]
    public void AutoDiscoversTheOnlyModelTheServerHasLoaded()
    {
        using var server = new FakeModelsServer("""{"data":[{"id":"llama3.1"}]}""");
        using var baseUrl = new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, server.BaseUrl);
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, null);
        using var directory = new TemporaryDirectory();

        var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("Local AI server"));
        Assert.Equal(Doctor.Level.Ok, ai.Level);
        Assert.Contains("llama3.1", ai.Message);
    }

    [Fact]
    public void ReportsAmbiguousModelsAsWarnListingTheChoices()
    {
        using var server = new FakeModelsServer("""{"data":[{"id":"llama3.1"},{"id":"qwen2.5-coder"}]}""");
        using var baseUrl = new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, server.BaseUrl);
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, null);
        using var directory = new TemporaryDirectory();

        var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("more than one loaded"));
        Assert.Equal(Doctor.Level.Warn, ai.Level);
        Assert.Contains("llama3.1", ai.Message);
        Assert.Contains("qwen2.5-coder", ai.Message);
    }

    [Fact]
    public void ReportsAnAvailableAiServerWithModelAsOk()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var baseUrl = new TemporaryEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, $"http://127.0.0.1:{port}");
        using var model = new TemporaryEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, "llama3.1");
        using var directory = new TemporaryDirectory();

        var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

        var ai = Assert.Single(results, result => result.Message.Contains("Local AI server"));
        Assert.Equal(Doctor.Level.Ok, ai.Level);
    }

    private sealed class FakeEnvironment : IEnvironment
    {
        public bool IsInteractive => false;
        public bool IsWsl2 => true;
        public string Architecture => "linux/amd64";
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

    /// <summary>A minimal real HTTP server for the one endpoint <see cref="LocalAiProvider.DiscoverModel"/>
    /// calls, since <see cref="Doctor"/> talks to a base URL rather than an injectable handler.</summary>
    private sealed class FakeModelsServer : IDisposable
    {
        private readonly System.Net.HttpListener listener;

        internal FakeModelsServer(string modelsResponseBody)
        {
            var port = FreeTcpPort();
            BaseUrl = $"http://127.0.0.1:{port}";
            listener = new System.Net.HttpListener();
            listener.Prefixes.Add(BaseUrl + "/");
            listener.Start();
            _ = Task.Run(() => Serve(modelsResponseBody));
        }

        internal string BaseUrl { get; }

        private void Serve(string modelsResponseBody)
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception exception) when (exception is System.Net.HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                var buffer = System.Text.Encoding.UTF8.GetBytes(modelsResponseBody);
                context.Response.ContentType = "application/json";
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
            }
        }

        private static int FreeTcpPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose() => listener.Stop();
    }
}
