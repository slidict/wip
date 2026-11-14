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
/// Order is the design. System32 and <c>ProgramFiles\WSL</c> need administrator rights to
/// write to, so whoever can write to the working directory cannot plant a decoy there. They
/// are tried first, and on a normal install one of them matches and the search stops.
/// </para>
/// <para>
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>, where the app execution alias lives, is
/// writable by anything running as the current user. It is last only because it is the one
/// candidate that reaches a Store-only install; dropping it would make wip report that WSL2
/// is not available on a machine where WSL works. It is a reachable location, not a safe one.
/// </para>
/// <para>
/// <c>scripts/Show-Wsl.ps1</c> also asks <c>Get-AppxPackage</c> for the package's install
/// location. That is deliberately not ported: C# would need the Appx/WinRT API or a
/// <c>powershell.exe</c> child, and the alias directory reaches the same install at the same
/// trust level. PATH is never consulted, and there is no fallback to a bare name.
/// </para>
/// </remarks>
public static class WslLauncher
{
    /// <summary>The trusted locations first, the user-writable alias directory last.</summary>
    public static IReadOnlyList<string> DefaultCandidates =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wsl.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wsl.exe"),
    ];

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
}
