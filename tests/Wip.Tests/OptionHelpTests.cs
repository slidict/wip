using System.CommandLine;
using Wip.Cli;

namespace Wip.Tests;

/// <summary>
/// <c>--help</c> is generated from the command tree, so an option with no <c>Description</c>
/// prints as a bare flag with nothing beside it, and neither the build nor the other tests say a
/// word about it. Walking the whole tree is what keeps the next option added from shipping that
/// way.
/// </summary>
public class OptionHelpTests
{
    [Fact]
    public void EveryOptionHasHelpText()
    {
        var missing =
            from command in Commands(Program.BuildRoot())
            from option in command.Options
            where string.IsNullOrWhiteSpace(option.Description)
            select $"{command.Name} {option.Name}";

        // Compared as joined text rather than as an empty collection so that a failure names
        // the options that are missing one.
        Assert.Equal("", string.Join(", ", missing));
    }

    private static IEnumerable<Command> Commands(Command command)
    {
        yield return command;
        foreach (var descendant in command.Subcommands.SelectMany(Commands))
        {
            yield return descendant;
        }
    }
}
