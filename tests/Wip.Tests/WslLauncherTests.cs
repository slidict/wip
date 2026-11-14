using Wip.Platform;

namespace Wip.Tests;

public class WslLauncherTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wip-wsl-launcher"));
    private static readonly string Trusted = Path.Combine(Root, "System32", "wsl.exe");
    private static readonly string Alias = Path.Combine(Root, "WindowsApps", "wsl.exe");

    [Fact]
    public void TheFirstExistingCandidateWins()
    {
        Assert.Equal(Trusted, WslLauncher.Resolve([Trusted, Alias], _ => true));
    }

    [Fact]
    public void ALaterCandidateIsUsedOnlyWhenTheEarlierOnesAreMissing()
    {
        Assert.Equal(Alias, WslLauncher.Resolve([Trusted, Alias], path => path == Alias));
    }

    [Fact]
    public void ANonRootedCandidateIsSkippedEvenWhenItExists()
    {
        Assert.Equal(Alias, WslLauncher.Resolve(["wsl.exe", Path.Combine("WSL", "wsl.exe"), Alias], _ => true));
    }

    [Fact]
    public void NothingFoundIsNull()
    {
        Assert.Null(WslLauncher.Resolve([Trusted, Alias], _ => false));
    }

    [Fact]
    public void TheDefaultsPutTheUserWritableAliasLast()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "the special folders are Windows paths");

        var candidates = WslLauncher.DefaultCandidates;
        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, candidate => Assert.True(Path.IsPathFullyQualified(candidate), candidate));
        Assert.Equal(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "wsl.exe"),
            candidates[0]);
        Assert.EndsWith(Path.Combine("Microsoft", "WindowsApps", "wsl.exe"), candidates[2], StringComparison.OrdinalIgnoreCase);
    }
}
