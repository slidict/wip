using System.CommandLine;
using Wip.Cli;

namespace Wip.Tests;

/// <summary>
/// Covers how an unrecognised first word gets routed to <c>dispatch</c>, so a name defined
/// under <c>commands:</c> in wip.yml can be run as <c>wip test</c>.
/// </summary>
public class CommandRoutingTests
{
    [Theory]
    // A real subcommand is left alone, wherever the global options sit.
    [InlineData(new[] { "up", "-d" }, "up")]
    [InlineData(new[] { "--config", "x.yml", "up" }, "up")]
    [InlineData(new[] { "--debug", "doctor" }, "doctor")]
    // An unknown name is routed.
    [InlineData(new[] { "mycmd" }, "dispatch")]
    [InlineData(new[] { "--config", "x.yml", "mycmd" }, "dispatch")]
    // Regression: the option's value equals the command name. Locating the command by
    // searching argv found the --config value instead and rewrote that, turning this into
    // `--config dispatch` and losing the command entirely.
    [InlineData(new[] { "--config", "test", "test" }, "dispatch")]
    [InlineData(new[] { "--env-file", "mycmd", "mycmd" }, "dispatch")]
    public void RoutesToTheRightCommand(string[] args, string expected)
    {
        var parsed = Program.Parse(Program.BuildRoot(), args);
        var command = parsed.CommandResult.Command;

        Assert.Equal(expected, command is RootCommand ? "" : command.Name);
    }

    /// <summary>
    /// An unrecognised option has to stay a usage error. Routing it into <c>dispatch</c>
    /// would turn "you typed a bad flag" into "no such command", naming the wrong thing.
    /// </summary>
    [Fact]
    public void UnknownOptionStaysAUsageError()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["--nosuchoption"]);

        Assert.IsType<RootCommand>(parsed.CommandResult.Command);
        Assert.NotEmpty(parsed.Errors);
        Assert.Contains("--nosuchoption", parsed.UnmatchedTokens);
    }

    [Fact]
    public void RoutingPreservesGlobalOptionsAndArguments()
    {
        var root = Program.BuildRoot();
        var parsed = Program.Parse(root, ["--config", "test", "test", "--", "extra"]);

        Assert.Equal("dispatch", parsed.CommandResult.Command.Name);

        var config = root.Options.OfType<Option<string?>>().Single(option => option.Name == "--config");
        Assert.Equal("test", parsed.GetValue(config));

        var name = parsed.CommandResult.Command.Arguments.OfType<Argument<string?>>().Single();
        Assert.Equal("test", parsed.GetValue(name));

        // The trailing arguments are what the custom command actually receives, so the
        // rewrite has to leave them intact and in order.
        var rest = parsed.CommandResult.Command.Arguments.OfType<Argument<string[]>>().Single();
        Assert.Equal<string>(["extra"], parsed.GetValue(rest) ?? []);
    }
}
