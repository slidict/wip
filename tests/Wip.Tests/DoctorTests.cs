using Wip.Ai;
using Wip.Configuration;
using Wip.Diagnostics;
using Wip.Platform;

namespace Wip.Tests;

public class DoctorTests
{
    [Fact]
    public void ReportsMissingAiHostAsWarnWithFixHint()
    {
        Environment.SetEnvironmentVariable(
            WindowsAiProvider.CommandEnvironmentVariable, "wip-ai-host-that-does-not-exist-anywhere");
        try
        {
            using var directory = new TemporaryDirectory();
            var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

            var ai = Assert.Single(results, result => result.Message.Contains("Windows AI host"));
            Assert.Equal(Doctor.Level.Warn, ai.Level);
            Assert.Contains("wip init --ai", ai.Message);
            Assert.Contains(WindowsAiProvider.CommandEnvironmentVariable, ai.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WindowsAiProvider.CommandEnvironmentVariable, null);
        }
    }

    [Fact]
    public void ReportsAnAvailableAiHostAsOk()
    {
        var realCommand = OperatingSystem.IsWindows() ? "cmd" : "sh";
        Environment.SetEnvironmentVariable(WindowsAiProvider.CommandEnvironmentVariable, realCommand);
        try
        {
            using var directory = new TemporaryDirectory();
            var results = new Doctor(new ConfigLoader(directory.Path), new FakeEnvironment()).Call();

            var ai = Assert.Single(results, result => result.Message.Contains("Windows AI host"));
            Assert.Equal(Doctor.Level.Ok, ai.Level);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WindowsAiProvider.CommandEnvironmentVariable, null);
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
}
