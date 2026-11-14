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

        // The package directories in between depend on the machine, so only the ends are fixed.
        var candidates = WslLauncher.DefaultCandidates;
        Assert.True(candidates.Count >= 3, string.Join("; ", candidates));
        Assert.All(candidates, candidate => Assert.True(Path.IsPathFullyQualified(candidate), candidate));
        Assert.Equal(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "wsl.exe"),
            candidates[0]);
        Assert.Equal(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wsl.exe"), candidates[^1]);
    }

    [Fact]
    public void PackageDirectoriesAreListedNewestNameFirst()
    {
        var windowsApps = Path.Combine(Root, "WindowsApps");
        string[] listed =
        [
            Path.Combine(windowsApps, "MicrosoftCorporationII.WindowsSubsystemForLinux_2.6.1.0_x64__8wekyb3d8bbwe"),
            Path.Combine(windowsApps, "MicrosoftCorporationII.WindowsSubsystemForLinux_3.0.5.0_x64__8wekyb3d8bbwe"),
        ];

        Assert.Equal(
            [Path.Combine(listed[1], "wsl.exe"), Path.Combine(listed[0], "wsl.exe")],
            WslLauncher.PackageCandidates(windowsApps, (_, _) => listed));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("security")]
    public void AnUnlistablePackageDirectoryContributesNothing(string failure)
    {
        Exception error = failure switch
        {
            "io" => new DirectoryNotFoundException(),
            "access" => new UnauthorizedAccessException(),
            _ => new System.Security.SecurityException(),
        };

        Assert.Empty(WslLauncher.PackageCandidates(Path.Combine(Root, "WindowsApps"), (_, _) => throw error));
    }

    [Fact]
    public void ARelativeWindowsAppsRootIsNotListed()
    {
        Assert.Empty(WslLauncher.PackageCandidates("WindowsApps",
            (_, _) => throw new InvalidOperationException("must not list the working directory")));
    }
}
