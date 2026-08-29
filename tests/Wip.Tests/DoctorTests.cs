using Wip.Ai;
using Wip.Configuration;
using Wip.Diagnostics;
using Wip.Platform;

namespace Wip.Tests;

public class DoctorTests
{
    [Fact]
    public void ReportsMissingAiServerAsWarnWithFixHint()
    {
        Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, "http://127.0.0.1:1");
        try
        {
            using var directory = new TemporaryDirectory();
            var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

            var ai = Assert.Single(results, result => result.Message.Contains("local AI server"));
            Assert.Equal(Doctor.Level.Warn, ai.Level);
            Assert.Contains("wip init --ai", ai.Message);
            Assert.Contains(LocalAiProvider.BaseUrlEnvironmentVariable, ai.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, null);
        }
    }

    [Fact]
    public void CallArgumentOverridesTheBaseUrlEnvironmentVariable()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, "http://127.0.0.1:1");
        Environment.SetEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, "llama3.1");
        try
        {
            using var directory = new TemporaryDirectory();
            var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment())
                .Call($"http://127.0.0.1:{port}");

            var ai = Assert.Single(results, result => result.Message.Contains("Local AI server"));
            Assert.Equal(Doctor.Level.Ok, ai.Level);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, null);
            listener.Stop();
        }
    }

    [Fact]
    public void ReportsMissingModelAsWarnWithFixHintWhenServerHasNoneLoaded()
    {
        using var server = new FakeModelsServer("""{"data":[]}""");
        Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, server.BaseUrl);
        try
        {
            using var directory = new TemporaryDirectory();
            var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

            var ai = Assert.Single(results, result => result.Message.Contains("No model configured"));
            Assert.Equal(Doctor.Level.Warn, ai.Level);
            Assert.Contains(LocalAiProvider.ModelEnvironmentVariable, ai.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, null);
        }
    }

    [Fact]
    public void AutoDiscoversTheOnlyModelTheServerHasLoaded()
    {
        using var server = new FakeModelsServer("""{"data":[{"id":"llama3.1"}]}""");
        Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, server.BaseUrl);
        try
        {
            using var directory = new TemporaryDirectory();
            var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

            var ai = Assert.Single(results, result => result.Message.Contains("Local AI server"));
            Assert.Equal(Doctor.Level.Ok, ai.Level);
            Assert.Contains("llama3.1", ai.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, null);
        }
    }

    [Fact]
    public void ReportsAmbiguousModelsAsWarnListingTheChoices()
    {
        using var server = new FakeModelsServer("""{"data":[{"id":"llama3.1"},{"id":"qwen2.5-coder"}]}""");
        Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, server.BaseUrl);
        try
        {
            using var directory = new TemporaryDirectory();
            var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

            var ai = Assert.Single(results, result => result.Message.Contains("more than one loaded"));
            Assert.Equal(Doctor.Level.Warn, ai.Level);
            Assert.Contains("llama3.1", ai.Message);
            Assert.Contains("qwen2.5-coder", ai.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, null);
        }
    }

    [Fact]
    public void ReportsAnAvailableAiServerWithModelAsOk()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, $"http://127.0.0.1:{port}");
        Environment.SetEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, "llama3.1");
        try
        {
            using var directory = new TemporaryDirectory();
            var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

            var ai = Assert.Single(results, result => result.Message.Contains("Local AI server"));
            Assert.Equal(Doctor.Level.Ok, ai.Level);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalAiProvider.BaseUrlEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(LocalAiProvider.ModelEnvironmentVariable, null);
            listener.Stop();
        }
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
