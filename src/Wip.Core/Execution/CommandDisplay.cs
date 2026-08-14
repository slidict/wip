using Wip.Platform;

namespace Wip.Execution;

/// <summary>
/// Renders a command array for debug output, masking <c>-e KEY=value</c> environment values
/// so secrets from wip.yml never reach logs.
/// </summary>
public static class CommandDisplay
{
    public static string ForDebug(IReadOnlyList<string> command)
    {
        var masked = new string[command.Count];
        for (var index = 0; index < command.Count; index++)
        {
            masked[index] = command[index];
        }

        for (var index = 0; index + 1 < command.Count; index++)
        {
            if (command[index] != "-e")
            {
                continue;
            }

            // A bare `-e NAME` with no '=' is rendered as "NAME=***" too. That reads as an
            // assignment the real command never had, but this string only ever reaches
            // --debug output, and diverging here would make debug logs stop matching what
            // users have seen from wip before.
            var pair = command[index + 1];
            var separator = pair.IndexOf('=');
            masked[index + 1] = $"{(separator < 0 ? pair : pair[..separator])}=***";
        }

        return Shellwords.Join(masked);
    }
}
