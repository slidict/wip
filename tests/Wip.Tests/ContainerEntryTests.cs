using Wip.Cli;

namespace Wip.Tests;

/// <summary>
/// Covers the step between wslc's listing and <see cref="CliContext.StatusLabel"/>: turning
/// <c>--format json</c> output into "is it there, and in what state".
/// </summary>
/// <remarks>
/// This layer had no tests at all, and that is exactly where the bug lived. StatusLabelTests
/// and ContainerActionTests take <c>exists</c> and <c>state</c> as inputs, so they passed on
/// a real machine where a running container was reported as "not found" — the parse handing
/// them <c>(false, null)</c> was never in question. The samples below are copied from real
/// WSLC runs in the end-to-end job -- 2.9.9 and the docker-shaped listing 2.9.10 replaced it
/// with -- rather than written from the shape wip expected.
/// </remarks>
public class ContainerEntryTests
{
    // static readonly rather than const: C# has no const of a nullable value type, and
    // the assertions compare against (bool, int?) tuples.
    private static readonly int? Created = 1;
    private static readonly int? Running = 2;

    /// <summary>What `wslc list --all --filter name=… --format json` actually prints.</summary>
    private const string SingleObject =
        """{"CreatedAt":1788077670,"Id":"0a32161acb23","Image":"wip-e2e:latest","Name":"wip-e2e-app","Ports":[],"State":2,"StateChangedAt":1788077670}""";

    [Fact]
    public void ReadsTheUnwrappedRecordWslcPrints()
    {
        Assert.Equal((true, Running), CliContext.ReadContainerEntry(SingleObject, "wip-e2e-app"));
    }

    /// <summary>Trailing newline included, since that is how it arrives from the pipe.</summary>
    [Fact]
    public void IgnoresSurroundingWhitespace()
    {
        Assert.Equal((true, Running), CliContext.ReadContainerEntry("\n" + SingleObject + "\n", "wip-e2e-app"));
    }

    private const string OtherObject = """{"Name":"other","State":1}""";

    [Fact]
    public void ReadsAnArrayOfRecords()
    {
        var output = "[" + SingleObject + "," + OtherObject + "]";
        Assert.Equal((true, Running), CliContext.ReadContainerEntry(output, "wip-e2e-app"));
        Assert.Equal((true, Created), CliContext.ReadContainerEntry(output, "other"));
    }

    [Fact]
    public void ReadsOneRecordPerLine()
    {
        var output = SingleObject + "\n" + OtherObject + "\n";
        Assert.Equal((true, Running), CliContext.ReadContainerEntry(output, "wip-e2e-app"));
        Assert.Equal((true, Created), CliContext.ReadContainerEntry(output, "other"));
    }

    /// <summary>
    /// `--filter name=` narrows a listing without promising an exact hit, so a record for a
    /// different container must not answer for this one.
    /// </summary>
    [Fact]
    public void ADifferentContainerIsNotThisOne()
    {
        Assert.Equal((false, (int?)null), CliContext.ReadContainerEntry(SingleObject, "wip-e2e-app-worker"));
    }

    [Fact]
    public void EmptyListingIsNotFound()
    {
        Assert.Equal((false, (int?)null), CliContext.ReadContainerEntry("[]", "wip-e2e-app"));
        Assert.Equal((false, (int?)null), CliContext.ReadContainerEntry("", "wip-e2e-app"));
        Assert.Equal((false, (int?)null), CliContext.ReadContainerEntry("null", "wip-e2e-app"));
    }

    /// <summary>A probe answers a question; unreadable output is "no", never an exception.</summary>
    [Fact]
    public void UnparseableOutputIsNotFound()
    {
        Assert.Equal((false, (int?)null), CliContext.ReadContainerEntry("not json at all", "wip-e2e-app"));
        Assert.Equal((false, (int?)null), CliContext.ReadContainerEntry("{\"Name\":", "wip-e2e-app"));
    }

    /// <summary>
    /// A listed container whose state wip cannot read is still listed: it exists, and the
    /// state falls through to "unknown" rather than making the container disappear.
    /// </summary>
    /// <remarks>
    /// The samples are states with no wslc equivalent, and a record carrying no state at all.
    /// A docker state name wslc does report is read, not treated as unreadable — see
    /// <see cref="ReadsTheDockerShapedListing"/>.
    /// </remarks>
    [Fact]
    public void UnreadableStateStillCountsAsPresent()
    {
        Assert.Equal(
            (true, (int?)null),
            CliContext.ReadContainerEntry("""{"Name":"wip-e2e-app","State":"paused"}""", "wip-e2e-app"));
        Assert.Equal(
            (true, (int?)null),
            CliContext.ReadContainerEntry("""{"Name":"wip-e2e-app","State":true}""", "wip-e2e-app"));
        Assert.Equal(
            (true, (int?)null),
            CliContext.ReadContainerEntry("""{"Name":"wip-e2e-app"}""", "wip-e2e-app"));
    }

    /// <summary>
    /// What wslc 2.9.10 prints for the same command, copied from the end-to-end job: the
    /// listing moved to docker's shape, so <c>Name</c> became <c>Names</c> and the numeric
    /// <c>State</c> became docker's name for it.
    /// </summary>
    /// <remarks>
    /// This is the regression the end-to-end job caught. Against 2.9.10 the parse read no
    /// name (falling back to whichever record came first) and no state at all, so `wip ps`
    /// called a running container "unknown", `wip up` tried to start one that was already
    /// running, and a `restart:` policy never saw an exited container.
    /// </remarks>
    private const string DockerShapedObject =
        """{"Command":"\"sleep 600\"","CreatedAt":"2026-09-05 01:09:48 +0000 UTC","HealthStatus":"","ID":"53054d746ba9","Image":"wip-e2e:latest","LocalVolumes":"0","Mounts":"","Names":"wip-e2e-app","Networks":"wip-e2e-net","Ports":"","RunningFor":"1 second ago","Size":"0B","State":"running","Status":"Up Less than a second"}""";

    [Fact]
    public void ReadsTheDockerShapedListing()
    {
        Assert.Equal((true, Running), CliContext.ReadContainerEntry(DockerShapedObject, "wip-e2e-app"));
        Assert.Equal((false, (int?)null), CliContext.ReadContainerEntry(DockerShapedObject, "wip-e2e-app-worker"));
    }

    /// <summary>Every state name wslc has a state of its own for.</summary>
    [Theory]
    [InlineData("created", 1)]
    [InlineData("running", 2)]
    [InlineData("exited", 3)]
    [InlineData("deleted", 4)]
    public void ReadsDockerStateNames(string reported, int expected)
    {
        Assert.Equal(
            (true, (int?)expected),
            CliContext.ReadContainerEntry($$"""{"Names":"wip-e2e-app","State":"{{reported}}"}""", "wip-e2e-app"));
    }

    /// <summary>
    /// docker writes several names for one container as a comma-separated list, and its API
    /// spelling carries a leading slash. Either has to still name the container.
    /// </summary>
    [Fact]
    public void ReadsOneOfSeveralNames()
    {
        Assert.Equal(
            (true, Running),
            CliContext.ReadContainerEntry("""{"Names":"other,wip-e2e-app","State":2}""", "wip-e2e-app"));
        Assert.Equal(
            (true, Running),
            CliContext.ReadContainerEntry("""{"Names":"/wip-e2e-app","State":2}""", "wip-e2e-app"));
    }

    /// <summary>
    /// Records with no Name at all fall back to the first one, so a renamed field degrades to
    /// the old behaviour instead of reporting every container as missing.
    /// </summary>
    [Fact]
    public void NamelessRecordsFallBackToTheFirst()
    {
        Assert.Equal((true, Running), CliContext.ReadContainerEntry("""{"State":2}""", "wip-e2e-app"));
    }

    [Fact]
    public void NetworksAreMatchedByName()
    {
        const string listing = """[{"Name":"wip-e2e-net"},{"Name":"bridge"}]""";
        Assert.True(CliContext.ListsNetwork(listing, "wip-e2e-net"));
        Assert.False(CliContext.ListsNetwork(listing, "wip-e2e"));
        Assert.True(CliContext.ListsNetwork("""{"Name":"wip-e2e-net"}""", "wip-e2e-net"));
        Assert.False(CliContext.ListsNetwork("", "wip-e2e-net"));
    }

    /// <summary>The network listing gets the same treatment: one spelling must not miss it.</summary>
    [Fact]
    public void NetworksAreMatchedUnderEitherFieldName()
    {
        Assert.True(CliContext.ListsNetwork("""{"Names":"wip-e2e-net"}""", "wip-e2e-net"));
        Assert.False(CliContext.ListsNetwork("""{"Names":"wip-e2e-network"}""", "wip-e2e-net"));
    }
}
