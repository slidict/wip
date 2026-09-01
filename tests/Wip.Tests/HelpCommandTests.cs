using System.CommandLine;
using Wip.Cli;

namespace Wip.Tests;

/// <summary>Covers <c>wip help</c>'s own argument validation and its reuse of <c>--help</c>'s
/// text. The AI round trip itself is covered by <see cref="LocalAiProvider"/>'s tests; wiring it
/// up here would mean either a real local AI server or duplicating those tests for no benefit.</summary>
public class HelpCommandTests
{
    [Fact]
    public void HelpTextMatchesRootHelpAndListsEveryCommand()
    {
        var text = Program.HelpText();

        Assert.Contains("Usage:", text);
        Assert.Contains("help", text);
        Assert.Contains("doctor", text);
        Assert.Contains("--ai", text);
    }

    [Fact]
    public void UrlWithoutAiIsRejected()
    {
        var parsed = Program.BuildRoot().Parse(["help", "--url", "http://localhost:1"]);
        var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };

        var exception = Assert.Throws<WipException>(() => parsed.Invoke(invocation));
        Assert.Equal("--url requires --ai", exception.Message);
    }

    [Fact]
    public void QuestionWithoutAiIsRejected()
    {
        var parsed = Program.BuildRoot().Parse(["help", "how", "do", "I", "sync"]);
        var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };

        var exception = Assert.Throws<WipException>(() => parsed.Invoke(invocation));
        Assert.Equal("a question requires --ai", exception.Message);
    }

    [Fact]
    public void PlainHelpRoutesToTheHelpCommandWithAiOff()
    {
        var parsed = Program.Parse(Program.BuildRoot(), ["help"]);

        Assert.Equal("help", parsed.CommandResult.Command.Name);
        var ai = parsed.CommandResult.Command.Options.OfType<Option<bool>>().Single(option => option.Name == "--ai");
        Assert.False(parsed.GetValue(ai));
    }

    [Fact]
    public void RemoteAiIsRejectedWithoutExplicitApprovalBeforeAnyNetworkRequest()
    {
        var parsed = Program.BuildRoot().Parse(
            ["help", "--ai", "--url", "https://ai.example.com/v1", "how do I sync"]);
        var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };

        var exception = Assert.Throws<WipException>(() => parsed.Invoke(invocation));
        Assert.Contains("--allow-remote-ai", exception.Message);
        Assert.Contains("ai.example.com", exception.Message);
    }

    [Fact]
    public void AllowRemoteAiFlagIsAvailableToAiCommands()
    {
        var root = Program.BuildRoot();

        Assert.Empty(root.Parse(["init", "--ai", "--allow-remote-ai"]).Errors);
        Assert.Empty(root.Parse(["help", "--ai", "--allow-remote-ai", "question"]).Errors);
    }
}
