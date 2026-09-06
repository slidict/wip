using System.Text.Json.Nodes;
using Wip.Tests.ComposeCompat;

namespace Wip.Tests;

/// <summary>
/// Reads one set of Compose fixtures twice — once as wip's <c>mode: compose-native</c>
/// resolves them, once as Microsoft's <c>wslc compose</c> would — and holds the two readings
/// against tests/compose-compat/matrix.json.
/// </summary>
/// <remarks>
/// <para>
/// Matching upstream is deliberately not the pass condition. <c>feature/compose</c> is an
/// in-progress POC, and freezing wip against it would pin wip to upstream's TODOs. What must
/// hold is that wip's own reading of each fixture is the recorded one, that the reference
/// reading of upstream is the recorded one, and that every difference between them is one the
/// matrix already names. A difference that appears, disappears, or changes shape fails here —
/// which is the signal to go and look at upstream again.
/// </para>
/// <para>
/// See tests/compose-compat/README.md for the workflow, and
/// tests/compose-compat/upstream/PINNED.md for what upstream is pinned at.
/// </para>
/// </remarks>
public class ComposeUpstreamCompatTests
{
    public static IEnumerable<object[]> Fixtures() => ComposeCompatCorpus.Fixtures();

    public static IEnumerable<object[]> MatrixEntries() => ComposeCompatMatrix.Entries();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void WipResolvesTheFixtureTheRecordedWay(string fixture)
    {
        Assert.SkipWhen(ComposeCompatCorpus.UpdateMode, "recording, not comparing");

        var expected = ComposeCompatCorpus.Expected(fixture);
        var actual = WipComposeInterpreter.Interpret(fixture).ToJson();

        Assert.Equal(Canonical(expected["wip"]), Canonical(actual));
    }

    /// <summary>
    /// wip's reading is taken from the argv it would run, so the argv itself is recorded
    /// beside it: a reviewer can check the interpretation against the command line, and a
    /// change to either shows up here.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void WipInvokesWslcTheRecordedWay(string fixture)
    {
        Assert.SkipWhen(ComposeCompatCorpus.UpdateMode, "recording, not comparing");

        var expected = ComposeCompatCorpus.Expected(fixture);
        Assert.Equal(Canonical(expected["wip_invocation"]), Canonical(WipComposeInterpreter.Invocation(fixture)));
    }

    /// <summary>
    /// Pins the reference model of upstream. It only changes when someone has gone and read
    /// <c>feature/compose</c> again — which is the point: the recorded upstream reading is
    /// what the matrix's differences are stated against.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void UpstreamReferenceResolvesTheFixtureTheRecordedWay(string fixture)
    {
        Assert.SkipWhen(ComposeCompatCorpus.UpdateMode, "recording, not comparing");

        var expected = ComposeCompatCorpus.Expected(fixture);
        var actual = UpstreamComposeInterpreter.Interpret(fixture).ToJson();

        Assert.Equal(Canonical(expected["upstream"]), Canonical(actual));
    }

    [Fact]
    public void MatrixCoversEveryFixtureAndNothingElse()
    {
        var matrix = ComposeCompatMatrix.Load();
        Assert.Equal(
            ComposeCompatCorpus.FixtureNames().ToList(),
            matrix.FixtureNames().Order().ToList());
    }

    [Fact]
    public void EveryTrackedFeatureIsExercisedBySomeFixture()
    {
        var matrix = ComposeCompatMatrix.Load();
        var covered = matrix.FixtureNames()
            .SelectMany(fixture => matrix.Features(fixture).Select(pair => pair.Key))
            .ToHashSet(StringComparer.Ordinal);

        var missing = ComposeSemanticModel.Features.Where(feature => !covered.Contains(feature)).ToList();
        Assert.True(missing.Count == 0, $"no fixture exercises: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(MatrixEntries))]
    public void MatrixEntryIsWellFormed(string fixture, string feature)
    {
        var matrix = ComposeCompatMatrix.Load();

        Assert.Contains(feature, ComposeSemanticModel.Features);
        Assert.Contains(matrix.Status(fixture, feature), ComposeCompatMatrix.Statuses);
        Assert.False(
            string.IsNullOrWhiteSpace(matrix.Note(fixture, feature)),
            $"{fixture}/{feature}: every matrix entry needs a note saying what the reader is looking at");
    }

    /// <summary>
    /// The load-bearing check. A <c>compatible</c> entry has to still be compatible, and a
    /// recorded difference has to still be a difference — a difference that quietly resolves
    /// itself means upstream moved and the matrix is now lying about it.
    /// </summary>
    [Theory]
    [MemberData(nameof(MatrixEntries))]
    public void MatrixStatusStillDescribesTheTwoInterpretations(string fixture, string feature)
    {
        Assert.SkipWhen(ComposeCompatCorpus.UpdateMode, "recording, not comparing");

        var matrix = ComposeCompatMatrix.Load();
        var wip = WipComposeInterpreter.Interpret(fixture);
        var upstream = UpstreamComposeInterpreter.Interpret(fixture);

        var wipView = Canonical(wip.Projection(feature));
        var upstreamView = Canonical(upstream.Projection(feature));
        var detail = $"{fixture}/{feature}\n  wip:      {wipView}\n  upstream: {upstreamView}";

        switch (matrix.Status(fixture, feature))
        {
            case "compatible":
                Assert.True(wipView == upstreamView, $"recorded compatible, but they differ:\n{detail}");
                break;

            case "known-difference":
                Assert.True(
                    wipView != upstreamView,
                    $"recorded as a known difference, but they now agree — re-read upstream and " +
                    $"update matrix.json:\n{detail}");
                break;

            case "upstream-unsupported":
                Assert.True(
                    !upstream.IsOk,
                    $"recorded as unsupported upstream, but upstream now reads the fixture — " +
                    $"re-read upstream and update matrix.json:\n{detail}");
                break;

            case "wip-only":
                Assert.True(
                    !upstream.IsOk,
                    $"recorded as wip-only, but upstream now reads the fixture — re-read " +
                    $"upstream and update matrix.json:\n{detail}");
                Assert.True(wip.IsOk, $"recorded as wip-only, but wip rejects the fixture:\n{detail}");
                break;

            default:
                throw new InvalidOperationException($"Unhandled status for {fixture}/{feature}");
        }
    }

    /// <summary>
    /// differences.md is generated from the matrix and the two interpretations, so it cannot
    /// drift from them the way a hand-maintained table would.
    /// </summary>
    [Fact]
    public void DifferencesDocumentIsUpToDate()
    {
        Assert.SkipWhen(ComposeCompatCorpus.UpdateMode, "recording, not comparing");

        Assert.Equal(
            File.ReadAllText(ComposeCompatCorpus.DifferencesPath).Replace("\r\n", "\n", StringComparison.Ordinal),
            ComposeCompatReport.Render());
    }

    /// <summary>
    /// Rewrites every recording from the current interpreters. Skipped unless
    /// <c>WIP_COMPOSE_COMPAT_UPDATE</c> is set, and every comparison above skips while it is —
    /// so a run either records or checks, never half of each.
    /// </summary>
    [Fact]
    public void RecordsInterpretations()
    {
        Assert.SkipUnless(
            ComposeCompatCorpus.UpdateMode,
            $"set {ComposeCompatCorpus.UpdateVariable}=1 to re-record");

        foreach (var fixture in ComposeCompatCorpus.FixtureNames())
        {
            var recording = new JsonObject
            {
                ["fixture"] = fixture,
                ["wip"] = WipComposeInterpreter.Interpret(fixture).ToJson(),
                ["upstream"] = UpstreamComposeInterpreter.Interpret(fixture).ToJson(),
                ["wip_invocation"] = WipComposeInterpreter.Invocation(fixture),
            };

            File.WriteAllText(ComposeCompatCorpus.ExpectedPath(fixture), ComposeCompatCorpus.Render(recording));
        }

        if (File.Exists(ComposeCompatCorpus.MatrixPath))
        {
            File.WriteAllText(ComposeCompatCorpus.DifferencesPath, ComposeCompatReport.Render());
        }
    }

    private static string Canonical(JsonNode? node) => node?.ToJsonString() ?? "null";
}
