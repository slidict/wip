using Wip.Platform;

namespace Wip.Tests;

/// <summary>
/// Exercises against real Windows LANGID values rather than <c>CultureInfo</c>: wip builds
/// with <c>InvariantGlobalization</c>, under which <c>CultureInfo.GetCultureInfo</c> throws for
/// every name but the invariant one, so a test built on it would not even compile a case for
/// "ja-JP" -- exactly the gap <see cref="DisplayLanguage"/> exists to work around.
/// </summary>
public class DisplayLanguageTests
{
    private const ushort EnglishUS = 0x0409;
    private const ushort EnglishUK = 0x0809;
    private const ushort JapaneseJP = 0x0411;
    private const ushort GermanDE = 0x0407;
    private const ushort FrenchFR = 0x040C;

    [Theory]
    [InlineData(EnglishUS, true)]
    [InlineData(EnglishUK, true)]
    [InlineData(JapaneseJP, false)]
    [InlineData(GermanDE, false)]
    [InlineData(FrenchFR, false)]
    public void MatchesOnlyTheEnglishPrimaryLanguageId(ushort languageId, bool expected)
    {
        var actual = DisplayLanguage.IsEnglish(languageId);

        Assert.Equal(expected, actual);
    }
}
