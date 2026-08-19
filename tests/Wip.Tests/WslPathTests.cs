using Wip.Platform;

namespace Wip.Tests;

/// <summary>
/// wslc resolves a <c>-v</c> source with <c>GetFullPathNameW</c> — as a Windows path — and
/// mounts an empty directory instead of failing when the result does not exist. Translating
/// a UNC working directory into <c>/home/u/proj</c> therefore produced a container with none
/// of the project in it and no error, which is the one failure mode a user cannot diagnose.
/// So the WSL filesystem is refused by name, and the two unmeasured alternatives stay
/// reachable through <c>WIP_WSL_PATH</c> for whoever runs the spike.
/// </summary>
public class WslPathTests
{
    private const string Unc = @"\\wsl.localhost\Ubuntu\home\u\proj";

    [Theory]
    [InlineData(Unc)]
    [InlineData(@"\\wsl$\Ubuntu\home\u\proj")]
    [InlineData(@"\\WSL.LOCALHOST\Ubuntu\home\u\proj")]
    public void WslPathsAreRefused(string path)
    {
        var exception = Assert.Throws<ConfigException>(() => WslPath.ForWslc(path, "sync.source", null));

        Assert.Contains("sync.source", exception.Message);
        Assert.Contains(path, exception.Message);
        Assert.Contains(WslPath.ModeVariable, exception.Message);
    }

    [Theory]
    [InlineData(@"C:\src\proj")]
    [InlineData(@"\\fileserver\share\proj")]
    [InlineData("/home/u/proj")]
    public void EveryOtherPathIsHandedOverUntouched(string path) =>
        Assert.Equal(path, WslPath.ForWslc(path, "sync.source", null));

    [Fact]
    public void UncModeHandsWslcTheUncPathUnchanged() =>
        Assert.Equal(Unc, WslPath.ForWslc(Unc, "sync.source", "unc"));

    [Theory]
    [InlineData(Unc, "/home/u/proj")]
    [InlineData(@"\\wsl.localhost\Ubuntu", "/")]
    [InlineData(@"\\wsl.localhost\Ubuntu\", "/")]
    public void LinuxModeRestoresTheOldTranslation(string path, string expected) =>
        Assert.Equal(expected, WslPath.ForWslc(path, "sync.source", "linux"));

    [Fact]
    public void ModeIsReadCaseInsensitively() =>
        Assert.Equal(Unc, WslPath.ForWslc(Unc, "sync.source", "UNC"));

    [Fact]
    public void AnUnknownModeSaysWhatTheModesAre()
    {
        var exception = Assert.Throws<ConfigException>(() => WslPath.ForWslc(Unc, "sync.source", "windows"));

        Assert.Contains(WslPath.ModeVariable, exception.Message);
        Assert.Contains("unc", exception.Message);
        Assert.Contains("linux", exception.Message);
    }

    /// <summary>
    /// The rule is Windows-only: on Linux — where the golden corpus was generated, and where
    /// wip runs against a native wslc — a path is already what wslc will be given.
    /// </summary>
    [Fact]
    public void OnlyWindowsAppliesTheRule()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ConfigException>(() => WslPath.ForWslc(Unc, "sync.source"));
            return;
        }

        Assert.Equal(Unc, WslPath.ForWslc(Unc, "sync.source"));
    }

    [Theory]
    [InlineData(Unc, "Ubuntu")]
    [InlineData(@"\\wsl$\Debian\home\u", "Debian")]
    [InlineData(@"C:\src\proj", null)]
    public void TheDistributionIsReadableFromTheUncPath(string path, string? expected) =>
        Assert.Equal(expected, WslPath.DistributionOf(path));
}
