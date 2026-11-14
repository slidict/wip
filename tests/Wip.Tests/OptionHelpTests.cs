using System.CommandLine;
using Wip.Cli;

namespace Wip.Tests;

/// <summary>
/// <c>--help</c> is generated from the command tree, so an option, subcommand or argument with no
/// <c>Description</c> prints as a bare name with nothing beside it, and neither the build nor the
/// other tests say a word about it. Walking the whole tree is what keeps the next one added from
/// shipping that way. The root is skipped for subcommands only: its description belongs to the
/// executable, not to a verb.
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

    [Fact]
    public void EverySubcommandHasHelpText()
    {
        var missing =
            from command in Commands(Program.BuildRoot()).Skip(1)
            where string.IsNullOrWhiteSpace(command.Description)
            select command.Name;

        Assert.Equal("", string.Join(", ", missing));
    }

    [Fact]
    public void EveryArgumentHasHelpText()
    {
        var missing =
            from command in Commands(Program.BuildRoot())
            from argument in command.Arguments
            where string.IsNullOrWhiteSpace(argument.Description)
            select $"{command.Name} {argument.Name}";

        Assert.Equal("", string.Join(", ", missing));
    }

    [Fact]
    public void LifecycleSummariesReadAsRemovalAndKeepPersistentVolumes()
    {
        var root = Program.BuildRoot();
        var volume = root.Subcommands.Single(command => command.Name == "volume");
        var reconcile = volume.Subcommands.Single(command => command.Name == "reconcile");
        var sandbox = root.Subcommands.Single(command => command.Name == "sandbox");
        var destroy = sandbox.Subcommands.Single(command => command.Name == "destroy");

        Assert.Contains("Remove", reconcile.Description!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creat", reconcile.Description!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exclusiv", destroy.Description!, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"(?i)\b(?:keep\w*|retain\w*|preserv\w*)\s+persistent\s+volumes\b|\bpersistent\s+volumes\s+(?:are\s+)?(?:kept|retained|preserved)\b", destroy.Description!);
    }

    /// <summary>
    /// System.CommandLine renders <c>[default: x]</c> itself for an option with a default, so a
    /// description that also states the default in prose prints it twice. 2.0.14 rendered nothing,
    /// which is how "(the default)" reached three descriptions unnoticed until the 3.0.0 bump.
    /// </summary>
    [Fact]
    public void NoOptionDescriptionRestatesItsDefault()
    {
        var duplicated =
            from command in Commands(Program.BuildRoot())
            from option in command.Options
            where option.Description?.Contains("the default", StringComparison.OrdinalIgnoreCase) == true
            select $"{command.Name} {option.Name}";

        Assert.Equal("", string.Join(", ", duplicated));
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
