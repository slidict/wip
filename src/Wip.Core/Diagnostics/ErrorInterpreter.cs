using System.Text.RegularExpressions;

namespace Wip.Diagnostics;

/// <summary>Translates raw WSLC error output into friendlier hints.</summary>
public sealed partial class ErrorInterpreter
{
    private readonly string architecture;

    public ErrorInterpreter(string architecture) => this.architecture = architecture;

    /// <summary>Returns a hint for <paramref name="output"/>, or null when nothing matches.</summary>
    public string? Interpret(string output)
    {
        if (VolumeLimitReached().IsMatch(output))
        {
            return VolumeLimitMessage;
        }

        if (RegistryRejected().IsMatch(output))
        {
            return RegistryMessage;
        }

        if (ArchitectureMismatch().IsMatch(output))
        {
            return ArchitectureMessage();
        }

        return RsyncMissing().IsMatch(output) ? RsyncMessage : null;
    }

    private const string VolumeLimitMessage = """
        The WSLC session has reached its mounted-volume limit.

        Stop any containers you no longer need, then restart the session:

          wslc container list
          wslc container stop <container-name>
          wslc system session terminate

        Then retry the command.

        """;

    private const string RsyncMessage = """
        `wip sync` needs rsync inside the image.

        Install it in your Dockerfile:

          RUN apt-get update && apt-get install -y rsync

        Or point sync.command at a tool the image already has.

        """;

    private const string RegistryMessage = """
        The container registry rejected the request.

        Try logging in with:

          wslc registry login -u <username> docker.io

        """;

    private string ArchitectureMessage() => $"""
        The image does not contain a manifest for the current CPU architecture.

        Current architecture:
          {architecture}

        Inspect the image with:

          docker buildx imagetools inspect <image>

        Rebuild and push a multi-platform image with:

          docker buildx build \
            --platform linux/amd64,linux/arm64 \
            -t <image> \
            --push .

        """;

    // Shells report a missing rsync as "rsync: not found", while the container runtime
    // names the executable either before or after its own phrasing.
    [GeneratedRegex(@"rsync: (?:command )?not found|rsync[^\n]*executable file not found|executable file not found[^\n]*rsync",
        RegexOptions.IgnoreCase)]
    private static partial Regex RsyncMissing();

    [GeneratedRegex(@"0x8007000e|too many mounted volumes|マウントされているボリュームが多すぎます",
        RegexOptions.IgnoreCase)]
    private static partial Regex VolumeLimitReached();

    [GeneratedRegex(@"pull access denied|insufficient_scope|authorization failed", RegexOptions.IgnoreCase)]
    private static partial Regex RegistryRejected();

    [GeneratedRegex(@"no matching manifest for linux/(?:amd64|arm64)", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureMismatch();
}
