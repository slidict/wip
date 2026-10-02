using System.CommandLine;
using Wip.Cli;

namespace Wip.Tests;

public class SandboxCommandTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("status")]
    [InlineData("destroy")]
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

    [Fact]
    public void MissingExecutableIsUsageError()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["sandbox", "exec", "first"]);
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
