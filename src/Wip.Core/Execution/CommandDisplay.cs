using Wip.Platform;

namespace Wip.Execution;

/// <summary>
/// Renders a command array for debug output, masking values carried by environment and build
/// secret options so secrets from configuration never reach logs.
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

        for (var index = 0; index < command.Count; index++)
        {
            if (command[index] is "-e" or "--env" or "--build-arg" or "--secret")
            {
                if (index + 1 < command.Count)
                {
                    masked[index + 1] = MaskAssignment(command[index + 1]);
                    index++;
                }

                continue;
            }

            foreach (var option in SensitiveLongOptions)
            {
                var prefix = $"{option}=";
                if (command[index].StartsWith(prefix, StringComparison.Ordinal))
                {
                    masked[index] = $"{prefix}***";
                    break;
                }
            }
        }

        return Shellwords.Join(masked);
    }

    private static readonly string[] SensitiveLongOptions = ["--env", "--build-arg", "--secret"];

    private static string MaskAssignment(string value)
    {
        var separator = value.IndexOf('=');
        return $"{(separator < 0 ? value : value[..separator])}=***";
    }
}
