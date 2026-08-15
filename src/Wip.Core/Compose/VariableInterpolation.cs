using System.Text.RegularExpressions;

namespace Wip.Compose;

/// <summary>
/// Interpolates <c>${VAR}</c> references the way <c>docker compose</c> does when reading
/// compose.yml: <c>${VAR}</c>, <c>${VAR:-default}</c> / <c>${VAR-default}</c>, bare
/// <c>$VAR</c>, and <c>$$</c> as an escaped literal dollar sign.
/// </summary>
/// <remarks>
/// <c>${VAR:?err}</c> and <c>${VAR:+alt}</c> are not recognised by the pattern at all, so —
/// unlike an unset <c>$VAR</c>, which resolves to an empty string — they pass through
/// completely unchanged, braces and all.
/// </remarks>
public static partial class VariableInterpolation
{
    public static string Call(string text, IReadOnlyDictionary<string, string> environment) =>
        Pattern().Replace(text, match => Substitute(match, environment));

    private static string Substitute(Match match, IReadOnlyDictionary<string, string> environment)
    {
        if (match.Value == "$$")
        {
            return "$";
        }

        var braced = match.Groups[1];
        var name = braced.Success ? braced.Value : match.Groups[5].Value;
        var found = environment.TryGetValue(name, out var value);

        var operatorGroup = match.Groups[3];
        if (!operatorGroup.Success)
        {
            return found ? value! : string.Empty;
        }

        var fallback = match.Groups[4].Value;
        return operatorGroup.Value switch
        {
            // ':-' also falls back for a set-but-empty value; '-' only for a missing one.
            ":-" => !found || value!.Length == 0 ? fallback : value!,
            _ => found ? value! : fallback,
        };
    }

    [GeneratedRegex(@"\$\$|\$\{([A-Za-z_][A-Za-z0-9_]*)((:-|-)([^}]*))?\}|\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Pattern();
}
