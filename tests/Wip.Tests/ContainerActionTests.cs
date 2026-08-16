using Wip.Cli;

namespace Wip.Tests;

/// <summary>
/// `wslc list --all` reports containers in every state, so a container being listed does not
/// mean it can be started. `start` on a running or deleted container fails with
/// ERROR_INVALID_STATE — which a plain `wip up` used to surface as an unexplained
/// "not in an appropriate state" after a successful build.
/// </summary>
public class ContainerActionTests
{
    private const int Invalid = 0;
    private const int Created = 1;
    private const int Running = 2;
    private const int Exited = 3;
    private const int Deleted = 4;

    [Fact]
    public void MissingContainerIsCreated()
    {
        Assert.Equal(
            CliContext.ContainerAction.Create,
            CliContext.DecideContainerAction(exists: false, state: null));
    }

    [Theory]
    [InlineData(Created)]
    [InlineData(Exited)]
    public void ListedAndStartableIsStarted(int state)
    {
        Assert.Equal(
            CliContext.ContainerAction.Start,
            CliContext.DecideContainerAction(exists: true, state: state));
    }

    [Fact]
    public void RunningContainerIsLeftAlone()
    {
        Assert.Equal(
            CliContext.ContainerAction.AlreadyRunning,
            CliContext.DecideContainerAction(exists: true, state: Running));
    }

    /// <summary>
    /// Listed but gone. Starting it cannot work, so the only way forward is to recreate it —
    /// which is what wslc's own state list means by `deleted`.
    /// </summary>
    [Fact]
    public void DeletedContainerIsRecreated()
    {
        Assert.Equal(
            CliContext.ContainerAction.Create,
            CliContext.DecideContainerAction(exists: true, state: Deleted));
    }

    /// <summary>
    /// A state wip cannot read is a reason to keep doing what used to work, not to start
    /// removing and recreating containers on a guess. `invalid` is included deliberately: it
    /// is not a state wslc parks a container in, and treating it as "recreate" would risk
    /// destroying one over a transient read.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(Invalid)]
    [InlineData(99)]
    public void UnreadableStateKeepsTheOldBehaviour(int? state)
    {
        Assert.Equal(
            CliContext.ContainerAction.Start,
            CliContext.DecideContainerAction(exists: true, state: state));
    }

    /// <summary>
    /// Existence wins over any state: a container that is not listed is created regardless of
    /// what a stale state value might say.
    /// </summary>
    [Theory]
    [InlineData(Running)]
    [InlineData(Exited)]
    [InlineData(Deleted)]
    public void AbsenceOutranksState(int state)
    {
        Assert.Equal(
            CliContext.ContainerAction.Create,
            CliContext.DecideContainerAction(exists: false, state: state));
    }
}
