using System.Runtime.InteropServices;

namespace Wip.Platform;

/// <summary>Whether Windows' own UI language is English.</summary>
/// <remarks>
/// wip builds with <c>InvariantGlobalization</c> (see <c>Directory.Build.props</c>) —
/// deliberately, since that is exactly what keeps wip's own strings simple to hold
/// culture-invariant (see <see cref="Diagnostics.Log"/>) — but the same setting also means
/// <c>CultureInfo.CurrentUICulture</c> never reflects the real OS language: every culture
/// collapses to the invariant one under it. Reading the language straight from Win32 instead
/// (<c>GetUserDefaultUILanguage</c>, the same LANGID <c>FormatMessage</c> itself consults to
/// render an HRESULT like <c>WSLC_E_IMAGE_NOT_FOUND</c>) is what still lets issue #134's locale
/// note tell an English desktop apart from one where a shelled-out command's raw output is not
/// in English.
/// </remarks>
public static partial class DisplayLanguage
{
    // The low 10 bits of a LANGID are its primary language, independent of sublanguage
    // (en-US, en-GB, ...); 0x09 is LANG_ENGLISH, per the language identifier constants in
    // the Windows SDK's winnt.h.
    private const ushort PrimaryLanguageMask = 0x3FF;
    private const ushort EnglishPrimaryLanguageId = 0x09;

    public static bool IsEnglish() => IsEnglish(CurrentUiLanguageId());

    internal static bool IsEnglish(ushort languageId) => (languageId & PrimaryLanguageMask) == EnglishPrimaryLanguageId;

    private static ushort CurrentUiLanguageId() =>
        OperatingSystem.IsWindows() ? GetUserDefaultUILanguage() : EnglishPrimaryLanguageId;

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
