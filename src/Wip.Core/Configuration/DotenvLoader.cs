using System.Text.RegularExpressions;

namespace Wip.Configuration;

/// <summary>
/// Parses a .env file the way <c>docker compose</c> does, so values don't have to be
/// duplicated into wip.yml just to reach the container as <c>-e</c> flags.
/// </summary>
public sealed partial class DotenvLoader
{
    private readonly string path;

    public DotenvLoader(string path) => this.path = path;

    /// <summary>
    /// Returns the parsed pairs in file order. Order is load-bearing: it decides the
    /// sequence of <c>-e</c> flags on the generated wslc command line.
    /// </summary>
    public OrderedDictionary<string, string> Load()
    {
        var result = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var match = LinePattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            result[match.Groups[1].Value] = Unquote(match.Groups[2].Value);
        }

        return result;
    }

    private static string Unquote(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 &&
            ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\''))))
        {
            return value[1..^1];
        }

        // An unquoted value ends at the first whitespace-preceded '#'. A '#' with no
        // space before it is part of the value, matching compose's own reading.
        var comment = TrailingComment().Match(value);
        return (comment.Success ? value[..comment.Index] : value).Trim();
    }

    [GeneratedRegex(@"\A(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=(.*)\z")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"\s+#")]
    private static partial Regex TrailingComment();
}
