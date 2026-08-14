using System.Runtime.InteropServices;

namespace Wip.Platform;

/// <summary>Detects the WSL2 and architecture facts wip branches on.</summary>
/// <remarks>
/// wip.exe runs on the Windows side, so the checks the Ruby build made from inside a
/// distribution — reading <c>/proc/version</c>, testing for the interop binfmt handler —
/// have no analogue and are simply gone. What is left is asking Windows itself whether the
/// WSL2 backend WSLC depends on is present.
/// </remarks>
public sealed class WindowsEnvironment : IEnvironment
{
    private readonly Lazy<bool> wsl2;

    public WindowsEnvironment() => wsl2 = new Lazy<bool>(DetectWsl2);

    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public bool IsWsl2 => wsl2.Value;

    /// <summary>
    /// The platform wslc would pull images for. Reported as a Linux platform because the
    /// containers run inside the WSL2 VM, whatever the host CPU is called on Windows.
    /// </summary>
    public string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "linux/amd64",
        System.Runtime.InteropServices.Architecture.Arm64 => "linux/arm64",
        var other => $"linux/{other.ToString().ToLowerInvariant()}",
    };

    /// <summary>
    /// Asks Windows whether the WSL2 backend is present.
    /// </summary>
    /// <remarks>
    /// A zero exit from <c>wsl.exe --status</c> means WSL is installed; it does not prove the
    /// default version is 2. Distinguishing them means parsing that output, which is
    /// localised and UTF-16 encoded, so it needs to be checked against a real machine before
    /// being relied on -- see docs/csharp-migration-plan.md §11. The Ruby build made exactly
    /// this check, so this is no less accurate than what it replaced.
    /// </remarks>
    private static bool DetectWsl2() => ProcessProbe.Succeeds("wsl.exe", ["--status"]);
}
