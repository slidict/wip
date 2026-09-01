using Wip.Execution;

namespace Wip.Tests;

[Collection("Environment variables")]
public sealed class CommandResolverTests
{
    [Fact]
    public void ResolveReturnsAbsolutePathForExecutableFoundOnPath()
    {
        using var directory = new TemporaryDirectory();
        var commandName = "wip-command-resolver-test";
        var executable = Path.Combine(directory.Path, commandName);
        File.WriteAllText(executable, string.Empty);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory.Path);

            var resolved = new CommandResolver([commandName]).Resolve();

            Assert.Equal(Path.GetFullPath(executable), resolved);
            Assert.True(Path.IsPathFullyQualified(resolved));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void ResolveReturnsPathProducedByExecutableResolver()
    {
        var actual = Path.GetFullPath(Path.Combine("tools", "wslc.EXE"));
        var resolver = new CommandResolver(["wslc"], resolveExecutable: candidate =>
            candidate == "wslc" ? actual : null);

        Assert.Equal(actual, resolver.Resolve());
    }

    [Fact]
    public void ResolveReturnsPathextCompletedFileNameOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "wip-pathext-test.WIPTEST");
        File.WriteAllText(executable, string.Empty);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalPathext = Environment.GetEnvironmentVariable("PATHEXT");
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory.Path);
            Environment.SetEnvironmentVariable("PATHEXT", ".WIPTEST");

            var resolved = new CommandResolver(["wip-pathext-test"]).Resolve();

            Assert.Equal(Path.GetFullPath(executable), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PATHEXT", originalPathext);
        }
    }

    [Fact]
    public void ResolvedPathIsNotReplacedBySameNamedFileInNewWorkingDirectory()
    {
        using var pathDirectory = new TemporaryDirectory();
        using var workingDirectory = new TemporaryDirectory();
        const string commandName = "wip-stable-resolution-test";
        var pathExecutable = Path.Combine(pathDirectory.Path, commandName);
        File.WriteAllText(pathExecutable, "PATH");
        File.WriteAllText(Path.Combine(workingDirectory.Path, commandName), "working directory");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.SetEnvironmentVariable("PATH", pathDirectory.Path);
            var resolved = new CommandResolver([commandName]).Resolve();
            Environment.CurrentDirectory = workingDirectory.Path;

            Assert.Equal(Path.GetFullPath(pathExecutable), resolved);
            Assert.Equal("PATH", File.ReadAllText(resolved));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void ExplicitRelativePathIsFrozenAsAbsolutePath()
    {
        using var directory = new TemporaryDirectory();
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory.Path;
            var relative = Path.Combine("bin", "tool");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(relative))!);
            File.WriteAllText(relative, string.Empty);

            var resolved = new CommandResolver([]).Resolve(relative);
            Environment.CurrentDirectory = originalDirectory;

            Assert.Equal(Path.Combine(directory.Path, "bin", "tool"), resolved);
            Assert.True(File.Exists(resolved));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
