namespace Wip.Platform;

/// <summary>Decides which <c>wsl.exe</c> wip runs; the C# twin of <c>Resolve-WslLauncher</c>.</summary>
/// <remarks>
/// <para>
/// The launcher is named by absolute path. With <c>UseShellExecute = false</c> a bare name
/// reaches CreateProcess as <c>lpApplicationName = null</c>, and that search order places the
/// current directory ahead of System32 -- and wip runs from inside a project directory, so a
/// <c>wsl.exe</c> committed to a cloned repository would run with the user's privileges.
/// </para>
/// <para>
/// Order is the design. System32, <c>ProgramFiles\WSL</c> and the Store package's install
/// directory under <c>ProgramFiles\WindowsApps</c> need administrator rights to write to, so
/// whoever can write to the working directory cannot plant a decoy there. They are tried
/// first, and on a normal install one of them matches and the search stops.
/// </para>
/// <para>
/// <c>scripts/Show-Wsl.ps1</c> finds the package directory through <c>Get-AppxPackage</c>.
/// Here it is found by listing <c>WindowsApps</c> instead, because C# would need the Appx/WinRT
/// API or a <c>powershell.exe</c> child. That listing usually fails for a normal user (the
/// directory's ACL is restricted), so it is best-effort and a failure falls through.
/// </para>
/// <para>
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>, where the app execution alias lives, is
/// writable by anything running as the current user. It is last only because it is what
/// reaches a Store-only install when the package directory cannot be listed; dropping it would
/// make wip report that WSL2 is not available on a machine where WSL works. It covers that
/// install only while the alias is switched on in Settings. It is a reachable location, not a
/// safe one. PATH is never consulted, and there is no fallback to a bare name.
/// </para>
/// </remarks>
public static class WslLauncher
{
    private const string PackagePattern = "MicrosoftCorporationII.WindowsSubsystemForLinux_*__8wekyb3d8bbwe";

    /// <summary>The trusted locations first, the user-writable alias directory last.</summary>
    public static IReadOnlyList<string> DefaultCandidates
    {
        get
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
                Path.Combine(programFiles, "WSL", "wsl.exe"),
                .. PackageCandidates(Path.Combine(programFiles, "WindowsApps")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "wsl.exe"),
            ];
        }
    }

    /// <summary>Returns the first fully qualified candidate that exists as a file, or null.</summary>
    public static string? Resolve(IReadOnlyList<string>? candidates = null, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var candidate in candidates ?? DefaultCandidates)
        {
            // Rooted as well as present: an unset special folder yields a relative path, which
            // would reintroduce the current-directory search this exists to avoid.
            if (Path.IsPathFullyQualified(candidate) && exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>wsl.exe</c> in each WSL package directory under <paramref name="windowsApps"/>, highest
    /// name first -- an ordinal order, not a version-aware one, so two installed versions give the
    /// same answer every time rather than the newer one; empty when it cannot be listed.
    /// </summary>
    internal static string[] PackageCandidates(string windowsApps, Func<string, string, string[]>? list = null)
    {
        // A relative root would list the working directory.
        if (!Path.IsPathFullyQualified(windowsApps))
        {
            return [];
        }

        try
        {
            // Materialized here: a lazy enumeration would throw outside this catch.
            return (list ?? Directory.GetDirectories)(windowsApps, PackagePattern)
                .OrderByDescending(directory => Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)
                .Select(directory => Path.Combine(directory, "wsl.exe"))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return [];
        }
    }
}
