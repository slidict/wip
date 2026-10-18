using System.Text.RegularExpressions;

namespace Wip.Tests;

/// <summary>
/// Pins what docs/tapes has to keep: one tape per wiki page, a shell that actually
/// renders on the Windows recording host, the visual settings shared with
/// docs/demo.tape, and no infrastructure details in assets that ship publicly.
/// Recording itself needs VHS, ttyd and FFmpeg, so CI checks the sources instead.
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

    // docs/tapes ships in a public repository and the GIFs are published from it.
    private static readonly string[] PrivateMarkers =
    [
        "herdr", "relay", "worktrees", "slidict/workspace", "claude-main", "codex1", "agy3",
        @"c:\users", "/home/",
    ];

    private static string TapesDirectory { get; } = FindTapesDirectory();

    public static IEnumerable<object[]> SlugCases => Slugs.Select(slug => new object[] { slug });

    public static IEnumerable<object[]> PublicFileCases =>
        Directory.EnumerateFiles(TapesDirectory)
            .Select(path => Path.GetFileName(path))
            .Where(name => !name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(SlugCases))]
    public void TapeWritesItsOwnGif(string slug)
    {
        Assert.Contains($"Output docs/tapes/{slug}.gif", ReadTape(slug));
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
    public void EveryTapeHasAFixtureAndPreparationStep()
    {
        Assert.True(File.Exists(Path.Combine(TapesDirectory, "wip.yml")));
        Assert.True(File.Exists(Path.Combine(TapesDirectory, "prepare.sh")));
        foreach (var slug in Slugs)
        {
            Assert.Contains(slug, ReadFile(Path.Combine(TapesDirectory, "prepare.sh")));
        }
    }

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
