using System.Diagnostics;
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

    private static bool DetectWsl2()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("wsl.exe")
            {
                ArgumentList = { "--status" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
