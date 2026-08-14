using System.Reflection;

namespace Wip;

/// <summary>
/// The single source of truth is &lt;Version&gt; in Directory.Build.props, which the SDK
/// stamps into this assembly at build time; the release workflow checks it against the
/// git tag. Read from the informational version so a CI-supplied suffix survives.
/// </summary>
public static class WipVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(WipVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        // The SDK appends "+<commit sha>" when the build has source-link metadata.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
