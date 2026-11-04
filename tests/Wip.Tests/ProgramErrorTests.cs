using System.Runtime.CompilerServices;
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

    /// <summary>Stands in for the exit code a throwing command never produced.</summary>
    private const int NotReturned = -1;

    [Fact]
    public void AnUnexpectedFailureLogsTheStopFailureAndStillLeaves()
    {
        var failure = new InvalidOperationException("backend broke");
        failure.Data[ScanEngine.StopErrorKey] = StopFailure;

        var (thrown, _, error) = Run(() => ThrowFrom(failure));

        Assert.Same(failure, thrown);
        // Identity alone does not say the original stack survived. CA2200 already fails the build
        // on `throw exception;`, so this holds the promise even if that rule is ever suppressed.
        Assert.Contains(nameof(ThrowFrom), thrown!.StackTrace);
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
        // The scan's own failure comes first, as ScanEngine orders it in the rebuilt message, so
        // the first line read alone names the cause rather than the cleanup.
        Assert.True(
            error.IndexOf("scan went wrong", StringComparison.Ordinal)
                < error.IndexOf("sandbox stop also failed", StringComparison.Ordinal),
            "the scan failure must be logged before the stop failure");
    }

    [Fact]
    public void AWipExceptionWithoutAStopFailureLogsOnlyTheMessage()
    {
        var (_, code, error) = Run(() => throw new WipException("plain failure"));

        Assert.Equal(1, code);
        Assert.Contains("plain failure", error);
        Assert.DoesNotContain("sandbox stop also failed", error);
    }

    /// <summary>
    /// Throws from a frame the rethrow must keep. Not inlined, because the Release build would
    /// otherwise drop the frame and the assertion would fail for the wrong reason.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ThrowFrom(Exception failure) => throw failure;

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
            // Not 0: a command that threw must not look like one that returned success.
            return (exception, NotReturned, writer.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    /// <summary>A subclass ScanEngine does not rebuild, as a future one would be.</summary>
    private sealed class FutureWipException(string message) : WipException(message);
}
