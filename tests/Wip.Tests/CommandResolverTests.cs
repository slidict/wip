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
    public void ResolveSkipsARelativePathEntry()
    {
        using var directory = new TemporaryDirectory();
        const string commandName = "wip-relative-path-entry-test";
        var executable = Path.Combine(directory.Path, commandName);
        File.WriteAllText(executable, string.Empty);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, directory.Path);
        Assert.SkipWhen(Path.IsPathFullyQualified(relative), "the temporary directory is on another drive");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // The real PATH walk is what is under test, so no injected resolveExecutable.
            Environment.SetEnvironmentVariable("PATH", relative);
            Assert.Throws<CommandNotFoundException>(() => new CommandResolver([commandName]).Resolve());

            Environment.SetEnvironmentVariable("PATH", directory.Path);
            Assert.Equal(Path.GetFullPath(executable), new CommandResolver([commandName]).Resolve());
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
    public void RelativeConfiguredCommandIsRefusedEvenWhenTheFileExists()
    {
        using var directory = new TemporaryDirectory();
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            // A real file at the relative location, so only the guard can make this throw.
            Environment.CurrentDirectory = directory.Path;
            var relative = Path.Combine("bin", "tool");
            Directory.CreateDirectory(Path.Combine(directory.Path, "bin"));
            File.WriteAllText(Path.Combine(directory.Path, relative), string.Empty);

            var refused = Assert.Throws<CommandNotFoundException>(() => new CommandResolver([]).Resolve(relative));

            Assert.Contains($"\"{relative}\"", refused.Message);
            Assert.Contains("absolute path", refused.Message);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public void AbsoluteConfiguredCommandStillResolves()
    {
        using var directory = new TemporaryDirectory();
        var executable = Path.Combine(directory.Path, "bin", "tool");
        Directory.CreateDirectory(Path.Combine(directory.Path, "bin"));
        File.WriteAllText(executable, string.Empty);

        Assert.Equal(executable, new CommandResolver([]).Resolve(executable));
    }

    [Fact]
    public void BareConfiguredNameStillResolvesOnPath()
    {
        using var directory = new TemporaryDirectory();
        const string commandName = "wip-configured-bare-name-test";
        var executable = Path.Combine(directory.Path, commandName);
        File.WriteAllText(executable, string.Empty);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory.Path);

            Assert.Equal(Path.GetFullPath(executable), new CommandResolver([]).Resolve(commandName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Theory]
    [InlineData("C:wslc.exe")]
    [InlineData(@"C:tools\wslc.exe")]
    [InlineData(@"\wslc.exe")]
    [InlineData("/wslc.exe")]
    public void DriveRelativeAndRootRelativeConfiguredCommandsAreRefusedOnWindows(string configured)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "drive- and root-relative paths exist only on Windows");

        // Every value resolves to a file, so only the guard can make this throw.
        var resolver = new CommandResolver([], resolveExecutable: _ => Path.Combine(Path.GetTempPath(), "wslc.exe"));

        var refused = Assert.Throws<CommandNotFoundException>(() => resolver.Resolve(configured));
        Assert.Contains($"\"{configured}\"", refused.Message);
    }

    [Theory]
    [InlineData("./wslc")]
    [InlineData("../wslc")]
    public void InjectedResolverCannotRouteAroundTheGuard(string configured)
    {
        var resolver = new CommandResolver([], resolveExecutable: _ => Path.Combine(Path.GetTempPath(), "wslc"));

        Assert.Throws<CommandNotFoundException>(() => resolver.Resolve(configured));
    }

    [Fact]
    public void ResolveNormalizesRelativePathReturnedByExecutableResolver()
    {
        var relative = Path.Combine("tools", "wslc");
        var resolver = new CommandResolver(["wslc"], resolveExecutable: candidate =>
            candidate == "wslc" ? relative : null);

        var resolved = resolver.Resolve();

        Assert.Equal(Path.GetFullPath(relative), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public void UnnormalizableCandidateReportsCommandNotFoundInsteadOfThrowingIoException()
    {
        var resolver = new CommandResolver([]);

        // Absolute, so this reaches normalization rather than the relative-path guard.
        Assert.Throws<CommandNotFoundException>(() =>
            resolver.Resolve(Path.Combine(Path.GetTempPath(), "bin", "to\0ol")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-resolver-test-").FullName;

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

[CollectionDefinition("Environment variables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
