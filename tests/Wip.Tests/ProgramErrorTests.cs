using Wip.Cli;
using Wip.Security;

namespace Wip.Tests;

/// <summary>
/// When <see cref="ScanEngine"/> cannot rebuild an exception, the sandbox stop failure travels
/// only in <see cref="Exception.Data"/>. Both of the CLI's handlers have to log it, or the
/// operator never learns that the scanner may still be running.
/// </summary>
[Collection(ConsoleStateCollection.Name)]
public class ProgramErrorTests
{
    private const string StopFailure = "sandbox stop failed (exit 5)";

    [Fact]
    public void AnUnexpectedFailureLogsTheStopFailureAndStillLeaves()
    {
        var failure = new InvalidOperationException("backend broke");
        failure.Data[ScanEngine.StopErrorKey] = StopFailure;

        var (thrown, _, error) = Run(() => throw failure);

        Assert.Same(failure, thrown);
        Assert.Contains($"sandbox stop also failed, so the scanner may still be running: {StopFailure}", error);
    }

    [Fact]
    public void AWipExceptionSubclassLogsTheStopFailureAndTheMessage()
    {
        var failure = new FutureWipException("scan went wrong");
        failure.Data[ScanEngine.StopErrorKey] = StopFailure;

        var (thrown, code, error) = Run(() => throw failure);

        Assert.Null(thrown);
        Assert.Equal(1, code);
        Assert.Contains($"sandbox stop also failed, so the scanner may still be running: {StopFailure}", error);
        Assert.Contains("scan went wrong", error);
    }

    [Fact]
    public void AWipExceptionWithoutAStopFailureLogsOnlyTheMessage()
    {
        var (_, code, error) = Run(() => throw new WipException("plain failure"));

        Assert.Equal(1, code);
        Assert.Contains("plain failure", error);
        Assert.DoesNotContain("sandbox stop also failed", error);
    }

    private static (Exception? Thrown, int Code, string Error) Run(Func<int> command)
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);
        try
        {
            return (null, Program.Report(command), writer.ToString());
        }
        catch (Exception exception)
        {
            return (exception, 0, writer.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    /// <summary>A subclass ScanEngine does not rebuild, as a future one would be.</summary>
    private sealed class FutureWipException(string message) : WipException(message);
}
