using System.CommandLine;
using Wip.Cli;

namespace Wip.Tests;

public class SandboxCommandTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("status")]
    [InlineData("stop")]
    [InlineData("destroy")]
    [InlineData("attach")]
    public void NestedOperationsDoNotFallBackToDispatch(string operation)
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["--config", "example.yml", "sandbox", operation, "first"]);
        Assert.Empty(parsed.Errors);
        Assert.Equal(operation, parsed.CommandResult.Command.Name);
    }

    [Fact]
    public void DelimiterKeepsExecutableOptionsAndMetacharactersAsArgv()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["sandbox", "exec", "first", "--timeout", "17", "--", "printf", "%s", "spaces ; $()", "--config", "operand", ""]);
        Assert.Empty(parsed.Errors);
        var command = parsed.CommandResult.Command;
        var argv = Assert.IsType<Argument<string[]>>(command.Arguments.Single(a => a.Name == "argv"));
        Assert.Equal(["printf", "%s", "spaces ; $()", "--config", "operand", ""], parsed.GetValue(argv)!);
    }

    /// <summary>
    /// Every spelling of the flag -- including the bundled <c>-it</c> people type out of
    /// docker habit -- has to reach the same option, and none of them may be mistaken for
    /// the executable's own argv.
    /// </summary>
    [Theory]
    [InlineData("--interactive")]
    [InlineData("-i")]
    [InlineData("-t")]
    [InlineData("-it")]
    public void InteractiveFlagParsesInEverySpellingAndLeavesArgvAlone(string flag)
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["sandbox", "exec", "first", flag, "--", "bash", "-l"]);
        Assert.Empty(parsed.Errors);
        var command = parsed.CommandResult.Command;
        var interactive = Assert.IsType<Option<bool>>(command.Options.Single(o => o.Name == "--interactive"));
        Assert.True(parsed.GetValue(interactive));
        var argv = Assert.IsType<Argument<string[]>>(command.Arguments.Single(a => a.Name == "argv"));
        Assert.Equal(["bash", "-l"], parsed.GetValue(argv)!);
    }

    [Fact]
    public void ExecIsNonInteractiveUnlessAsked()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["sandbox", "exec", "first", "--", "true"]);
        Assert.Empty(parsed.Errors);
        var interactive = Assert.IsType<Option<bool>>(
            parsed.CommandResult.Command.Options.Single(o => o.Name == "--interactive"));
        Assert.False(parsed.GetValue(interactive));
    }

    /// <summary>
    /// An interactive session has no deadline, so a <c>--timeout</c> given alongside it is
    /// reported rather than silently dropped -- the caller would otherwise believe the
    /// session is bounded.
    /// </summary>
    [Fact]
    public void TimeoutWithInteractiveIsRejected()
    {
        var parsed = Program.BuildRoot().Parse(["sandbox", "exec", "first", "--interactive", "--timeout", "30", "--", "bash"]);
        var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };

        var exception = Assert.Throws<ConfigException>(() => parsed.Invoke(invocation));
        Assert.Contains("--interactive has no deadline", exception.Message);
    }

    /// <summary>
    /// Attach joins the process that is already running, so there is nothing to pass it; a
    /// command given anyway is a usage error rather than a silently dropped argument (the
    /// caller wanted <c>exec --interactive</c>).
    /// </summary>
    [Fact]
    public void AttachTakesNoCommand()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["sandbox", "attach", "first", "--", "bash"]);
        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void RelayParsesWithOptionalUpstream()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["sandbox", "relay", "first", "--upstream", "/host/herdr.sock"]);
        Assert.Empty(parsed.Errors);
        Assert.Equal("relay", parsed.CommandResult.Command.Name);
        Assert.Empty(Program.Parse(Program.BuildRoot(), ["sandbox", "relay", "first"]).Errors);
        Assert.NotEmpty(Program.Parse(Program.BuildRoot(), ["sandbox", "relay", "first", "--", "sh"]).Errors);
    }

    [Fact]
    public void MissingExecutableIsUsageError()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["sandbox", "exec", "first"]);
        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void ListTakesNoNameAndDoesNotFallBackToDispatch()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["--config", "example.yml", "sandbox", "list"]);
        Assert.Empty(parsed.Errors);
        Assert.Equal("list", parsed.CommandResult.Command.Name);
        Assert.Empty(parsed.CommandResult.Command.Arguments);
    }

    /// <summary>
    /// The listing covers every declared sandbox, so a name is not a shorter way to ask for
    /// one: it has to be a usage error rather than an argument the command quietly drops.
    /// </summary>
    [Fact]
    public void ListRejectsASandboxName()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["--config", "example.yml", "sandbox", "list", "first"]);
        Assert.NotEmpty(parsed.Errors);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("status")]
    [InlineData("destroy")]
    [InlineData("reconcile")]
    public void StorageOperationsParseWithoutDispatch(string operation)
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["volume", operation, "data"]);
        Assert.Empty(parsed.Errors);
        Assert.Equal(operation, parsed.CommandResult.Command.Name);
    }
}
