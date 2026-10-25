using System.Text.RegularExpressions;

namespace Wip.Tests;

/// <summary>
/// Pins what docs/tapes has to keep: one tape per wiki page with its recorded GIF, a
/// shell that actually renders on the Windows recording host, the visual settings
/// shared with docs/demo.tape, and no infrastructure details in assets that ship
/// publicly. Recording needs VHS, ttyd and FFmpeg, so CI checks the sources and the
/// committed GIFs rather than rendering them.
/// </summary>
public class WikiTapeTests
{
    public static readonly string[] Slugs =
    [
        "getting-started", "wip-up", "wip-build", "wip-shell", "wip-logs", "wip-sync", "wip-sandbox",
    ];

    // Settings the tapes share with docs/demo.tape so every recording looks the same.
    private static readonly string[] SharedSettings =
    [
        "FontSize", "Width", "Height", "Theme", "Padding", "TypingSpeed",
    ];

    // docs/tapes ships in a public repository and the wiki links straight to its raw URLs.
    // The tracker's name comes from PrivateTrackerReferenceTests rather than being written
    // here: that test searches every tracked file for it, and this repository is public, so
    // the name is assembled at run time instead of appearing in any file.
    private static readonly string[] PrivateMarkers =
    [
        "herdr", "relay", "worktrees", PrivateTrackerReferenceTests.PrivateTracker,
        "claude-main", "codex1", "agy3",
        @"c:\users", "c:/users", "/home/", "/root/",
    ];

    private static string TapesDirectory { get; } = FindTapesDirectory();

    public static IEnumerable<object[]> SlugCases => Slugs.Select(slug => new object[] { slug });

    public static IEnumerable<object[]> PublicFileCases =>
        Directory.EnumerateFiles(TapesDirectory)
            .Select(path => Path.GetFileName(path))
            .Where(name => !name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(name => new object[] { name });

    [Fact]
    public void ExactlyTheNamedTapesAreShipped()
    {
        Assert.Equal(
            Slugs.Order(),
            Directory.EnumerateFiles(TapesDirectory, "*.tape")
                .Select(Path.GetFileNameWithoutExtension).Order());
    }

    [Theory]
    [MemberData(nameof(SlugCases))]
    public void TapeWritesItsOwnGif(string slug)
    {
        Assert.Contains($"Output docs/tapes/{slug}.gif", ReadTape(slug));
    }

    /// <summary>
    /// The wiki links to https://raw.githubusercontent.com/.../docs/tapes/&lt;slug&gt;.gif, so a
    /// missing recording is a broken image on a published page, not a local inconvenience.
    /// </summary>
    [Theory]
    [MemberData(nameof(SlugCases))]
    public void TapeShipsItsRecordedGif(string slug)
    {
        var path = Path.Combine(TapesDirectory, $"{slug}.gif");
        Assert.True(File.Exists(path), $"Missing recorded GIF: docs/tapes/{slug}.gif");
    }

    [Fact]
    public void RecordedGifsAreNotIgnored()
    {
        Assert.False(File.Exists(Path.Combine(TapesDirectory, ".gitignore")));
    }

    /// <summary>
    /// Measured on a Windows recording host (VHS 0.12.1, ttyd 1.7.7, FFmpeg 9.0.2):
    /// a tape with Set Shell "bash" renders a GIF with no text in any frame, while the
    /// same tape with PowerShell renders normally. Windows is wip's shipping target.
    /// </summary>
    [Theory]
    [MemberData(nameof(SlugCases))]
    public void TapeRecordsInPowerShell(string slug)
    {
        Assert.Contains("Set Shell \"powershell\"", ReadTape(slug));
    }

    [Theory]
    [MemberData(nameof(SlugCases))]
    public void TapeKeepsTheSharedVisualSettings(string slug)
    {
        var demo = ReadFile(Path.Combine(TapesDirectory, "..", "demo.tape"));
        var tape = ReadTape(slug);
        foreach (var setting in SharedSettings)
        {
            var expected = SettingLine(demo, setting);
            Assert.Contains(expected, tape);
        }
    }

    [Theory]
    [MemberData(nameof(SlugCases))]
    public void ReadmeMapsTheTapeToItsWikiPage(string slug)
    {
        Assert.Contains(slug, Readme());
    }

    [Fact]
    public void ReadmeNamesTheRecordingToolchain()
    {
        var readme = Readme();
        foreach (var tool in new[] { "VHS", "ttyd", "FFmpeg" })
        {
            Assert.Contains(tool, readme, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReadmeShowsHowTheWikiReferencesARecording()
    {
        Assert.Contains("https://raw.githubusercontent.com/slidict/wip/main/docs/tapes/", Readme());
    }

    [Fact]
    public void EveryTapeHasAFixtureAndPreparationStep()
    {
        Assert.True(File.Exists(Path.Combine(TapesDirectory, "wip.yml")));
        Assert.True(File.Exists(Path.Combine(TapesDirectory, "prepare.sh")));
        foreach (var slug in Slugs)
        {
            Assert.Contains(slug, ReadFile(Path.Combine(TapesDirectory, "prepare.sh")));
        }
    }

    // GIFs are binary assets: their presence is checked above, never their bytes as text.
    [Theory]
    [MemberData(nameof(PublicFileCases))]
    public void PublicAssetsCarryNoInternalDetails(string name)
    {
        var text = ReadFile(Path.Combine(TapesDirectory, name));
        foreach (var marker in PrivateMarkers)
        {
            Assert.DoesNotContain(marker, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ReadTape(string slug) => ReadFile(Path.Combine(TapesDirectory, $"{slug}.tape"));

    private static string Readme() => ReadFile(Path.Combine(TapesDirectory, "README.md"));

    private static string ReadFile(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string SettingLine(string tape, string setting)
    {
        var match = Regex.Match(tape, $"^Set {setting} .+$", RegexOptions.Multiline);
        Assert.True(match.Success, $"docs/demo.tape has no 'Set {setting}' line to align with");
        return match.Value;
    }

    private static string FindTapesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "tapes");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find docs/tapes walking up from {AppContext.BaseDirectory}");
    }
}
