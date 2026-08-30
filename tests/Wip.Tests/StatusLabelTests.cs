using Wip.Cli;

namespace Wip.Tests;

/// <summary>
/// Covers <c>wip ps</c>/<c>wip status</c>'s state-to-label mapping. Pure function, mirroring
/// the WSLC container states documented alongside <see cref="CliContext.DecideContainerAction"/>:
/// 0 invalid, 1 created, 2 running, 3 exited, 4 deleted.
/// </summary>
public class StatusLabelTests
{
    private const int Invalid = 0;
    private const int Created = 1;
    private const int Running = 2;
    private const int Exited = 3;
    private const int Deleted = 4;

    [Theory]
    [InlineData(Running)]
    [InlineData(Exited)]
    [InlineData(Deleted)]
    public void AbsenceOutranksState(int state)
    {
        Assert.Equal("not found", CliContext.StatusLabel(exists: false, state: state));
    }

    [Fact]
    public void NotListedIsNotFound()
    {
        Assert.Equal("not found", CliContext.StatusLabel(exists: false, state: null));
    }

    [Fact]
    public void CreatedIsReported()
    {
        Assert.Equal("created", CliContext.StatusLabel(exists: true, state: Created));
    }

    [Fact]
    public void RunningIsReported()
    {
        Assert.Equal("running", CliContext.StatusLabel(exists: true, state: Running));
    }

    [Fact]
    public void ExitedIsReported()
    {
        Assert.Equal("exited", CliContext.StatusLabel(exists: true, state: Exited));
    }

    [Fact]
    public void DeletedIsReported()
    {
        Assert.Equal("deleted", CliContext.StatusLabel(exists: true, state: Deleted));
    }

    /// <summary>
    /// A state wip cannot read, or one wslc does not actually use, falls back to "unknown"
    /// rather than guessing — the same caution <see cref="CliContext.DecideContainerAction"/>
    /// takes for an unreadable state.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(Invalid)]
    [InlineData(99)]
    public void UnreadableStateIsUnknown(int? state)
    {
        Assert.Equal("unknown", CliContext.StatusLabel(exists: true, state: state));
    }
}
