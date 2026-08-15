namespace Wip.Platform;

/// <summary>Host facts wip branches on, behind an interface so tests can pin them.</summary>
public interface IEnvironment
{
    /// <summary>Whether both stdin and stdout are a terminal, deciding whether `-it` is passed.</summary>
    bool IsInteractive { get; }

    /// <summary>Whether the WSL2 backend WSLC depends on is available.</summary>
    bool IsWsl2 { get; }

    /// <summary>The platform string reported by diagnostics, e.g. "linux/amd64".</summary>
    string Architecture { get; }
}
