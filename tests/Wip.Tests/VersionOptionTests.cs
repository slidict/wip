using System.CommandLine;
using Wip.Cli;

namespace Wip.Tests;

/// <summary>
/// <c>wip --version</c> is the spelling most CLIs take, so wip accepts it — but it has to answer
/// the way <c>wip version</c> does rather than with the bare assembly version
/// System.CommandLine's own <c>--version</c> prints. Both halves are pinned here: exactly one
/// <c>--version</c> on the root, and the same report out of either spelling with no wip.yml in
/// sight.
/// </summary>
public class VersionOptionTests
{
    /// <summary>
    /// A path that cannot resolve, so the config load fails identically on every machine. That
    /// is the point of the two cases that use it: reporting a version is not allowed to depend
    /// on finding a wip.yml.
    /// </summary>
    private static readonly string MissingConfig =
        Path.Combine(Path.GetTempPath(), "wip-version-option-tests-no-such-file.yml");

    [Fact]
    public void RootHasExactlyOneVersionOptionWithHelpText()
    {
        var version = Program.BuildRoot().Options.Single(option => option.Name == "--version");

        Assert.False(string.IsNullOrWhiteSpace(version.Description));
    }

    [Fact]
    public void RootAcceptsVersionWithoutError()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["--version"]);

        Assert.Empty(parsed.Errors);
        Assert.IsType<RootCommand>(parsed.CommandResult.Command);
    }

    [Fact]
    public void VersionOptionWithVolumeDestroyIsRejectedDuringParsing()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["--version", "volume", "destroy", "NAME"]);

        Assert.Equal("--version cannot be combined with a subcommand.", Assert.Single(parsed.Errors).Message);
    }

    [Fact]
    public void VersionOptionReportsTheWipVersionWithoutAConfig()
    {
        var (code, output) = Run("--config", MissingConfig, "--version");

        Assert.Equal(0, code);
        Assert.Contains($"wip {WipVersion.Current}", output);
    }

    /// <summary>
    /// The option and the command run the same method, so this is about the contract rather
    /// than the wiring: whichever one a user reaches for, the answer and the exit code match.
    /// </summary>
    [Fact]
    public void VersionOptionAgreesWithTheVersionCommand()
    {
        var option = Run("--config", MissingConfig, "--version");
        var command = Run("--config", MissingConfig, "version");

        Assert.Equal(command.Code, option.Code);
        Assert.Equal(command.Output, option.Output);

        // Equality alone would also hold if both printed nothing at all, so one of the two
        // reports is still checked for the version it is supposed to carry.
        Assert.Contains($"wip {WipVersion.Current}", option.Output);
    }

    /// <summary>
    /// <see cref="CliContext.Version"/> prints through <see cref="Console"/> rather than the
    /// invocation's writer, so both are pointed at the same buffer here.
    /// </summary>
    private static (int Code, string Output) Run(params string[] args)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var invocation = new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
                Output = writer,
            };

            return (Program.Parse(Program.BuildRoot(), args).Invoke(invocation), writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
