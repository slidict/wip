using System.Diagnostics;
using System.Text;
using Wip.Execution;

namespace Wip.Ai;

/// <summary>
/// Windows-local AI provider. It speaks a deliberately tiny stdin/stdout protocol to the
/// Windows AI host so wip does not take a compile-time dependency on a model name or a
/// particular revision of the evolving Windows AI SDK.
/// </summary>
public sealed class WindowsAiProvider : IWipAiProvider
{
    public const string CommandEnvironmentVariable = "WIP_WINDOWS_AI_COMMAND";
    public const string DefaultCommand = "wip-windows-ai";
    private const int MaxResponseCharacters = 1024 * 1024;
    private readonly string command;

    public WindowsAiProvider(string? command = null) => this.command = ResolveCommand(command);

    /// <summary>The host executable name or path this provider (or a default instance) would use.</summary>
    public static string ResolveCommand(string? command = null) => command
        ?? Environment.GetEnvironmentVariable(CommandEnvironmentVariable)
        ?? DefaultCommand;

    /// <summary>
    /// Whether the Windows AI host can be found without starting it, so <c>wip doctor</c> and
    /// <c>wip init --ai</c> can report a missing host up front instead of only after the user
    /// has typed a whole request into a prompt that was never going anywhere.
    /// </summary>
    public static bool IsAvailable(string? command = null)
    {
        try
        {
            new CommandResolver([], "Windows AI host").Resolve(ResolveCommand(command));
            return true;
        }
        catch (CommandNotFoundException)
        {
            return false;
        }
    }

    public static string NotFoundMessage(string command) =>
        $"Windows AI host '{command}' was not found. Install the wip Windows AI host " +
        $"or set {CommandEnvironmentVariable} to its path.";

    public string Generate(string prompt, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("generate");

        try
        {
            using var process = Process.Start(start)
                ?? throw new WipException($"Could not start Windows AI host '{command}'");
            var outputTask = Task.Run(() => ReadBounded(process.StandardOutput, MaxResponseCharacters));
            var errorTask = Task.Run(() => ReadBounded(process.StandardError, 16 * 1024));
            process.StandardInput.Write(prompt);
            process.StandardInput.Close();

            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new WipException($"Windows AI host failed ({process.ExitCode}): {error.Trim()}");
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                throw new WipException("Windows AI host returned an empty response");
            }

            return output;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new WipException(NotFoundMessage(command), exception);
        }
    }

    private static string ReadBounded(TextReader reader, int limit)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        var exceeded = false;
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            // Keep draining past the limit so the host's pipe never fills and blocks
            // its exit; only stop accumulating into the result.
            if (!exceeded)
            {
                result.Append(buffer, 0, count);
                exceeded = result.Length > limit;
            }
        }

        if (exceeded)
        {
            throw new WipException("Windows AI host response exceeded wip's 1 MiB limit");
        }

        return result.ToString();
    }
}
