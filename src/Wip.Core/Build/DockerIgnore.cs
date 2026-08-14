namespace Wip.Build;

/// <summary>
/// Parses a .dockerignore file and decides whether a build-context-relative path should be
/// excluded, following the same pattern rules as the Docker CLI: gitignore-like globs,
/// later rules override earlier ones, <c>!</c> negates.
/// </summary>
public sealed class DockerIgnore
{
    private readonly List<Rule> rules;

    private DockerIgnore(IEnumerable<string> lines) =>
        rules = lines.Select(Parse).OfType<Rule>().ToList();

    public static DockerIgnore Load(string path) =>
        new(File.Exists(path) ? File.ReadAllLines(path) : []);

    public static DockerIgnore FromLines(IEnumerable<string> lines) => new(lines);

    public bool IsEmpty => rules.Count == 0;

    /// <summary>
    /// A match on a directory component also excludes everything under it, so every prefix
    /// of the path is tested rather than only the full path.
    /// </summary>
    public bool Ignored(string relativePath)
    {
        var components = relativePath.Split('/');
        var ignored = false;
        foreach (var rule in rules)
        {
            if (MatchesAnyPrefix(rule.Pattern, components))
            {
                ignored = !rule.Negate;
            }
        }

        return ignored;
    }

    /// <summary>
    /// Whether a walker can stop descending into an ignored directory without risking a
    /// miss: false when some later negated rule (for example <c>!node_modules/pkg</c> after
    /// <c>node_modules</c>) could still match something beneath it.
    /// </summary>
    public bool Prunable(string relativePath)
    {
        var components = relativePath.Split('/');
        return !rules.Any(rule => rule.Negate && TargetsDescendant(rule.Pattern, components));
    }

    private static bool MatchesAnyPrefix(string[] pattern, string[] components)
    {
        for (var length = 1; length <= components.Length; length++)
        {
            if (Match(pattern, 0, components, 0, length))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TargetsDescendant(string[] pattern, string[] directoryComponents)
    {
        if (pattern.Length > 0 && pattern[0] == "**")
        {
            return true;
        }

        if (pattern.Length <= directoryComponents.Length)
        {
            return false;
        }

        return !directoryComponents.Where((component, index) => !MatchComponent(pattern[index], component)).Any();
    }

    /// <summary>
    /// Matches pattern components against path components. <c>**</c> spans any number of
    /// components, including none; every other pattern component matches exactly one, which
    /// is what gives <c>*</c> its "does not cross a slash" behaviour for free.
    /// </summary>
    private static bool Match(string[] pattern, int patternIndex, string[] path, int pathIndex, int pathLength)
    {
        while (true)
        {
            if (patternIndex == pattern.Length)
            {
                return pathIndex == pathLength;
            }

            if (pattern[patternIndex] == "**")
            {
                for (var skip = pathIndex; skip <= pathLength; skip++)
                {
                    if (Match(pattern, patternIndex + 1, path, skip, pathLength))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pathIndex == pathLength || !MatchComponent(pattern[patternIndex], path[pathIndex]))
            {
                return false;
            }

            patternIndex++;
            pathIndex++;
        }
    }

    /// <summary>
    /// Glob matching within a single path component: <c>*</c>, <c>?</c>, and <c>[...]</c>
    /// classes. Leading dots are ordinary characters here, matching the Docker CLI (and the
    /// <c>FNM_DOTMATCH</c> the Ruby build passed).
    /// </summary>
    private static bool MatchComponent(string pattern, string component)
    {
        var patternIndex = 0;
        var componentIndex = 0;
        var starPattern = -1;
        var starComponent = 0;

        while (componentIndex < component.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starPattern = patternIndex++;
                starComponent = componentIndex;
            }
            else if (patternIndex < pattern.Length && MatchLiteral(pattern, ref patternIndex, component[componentIndex]))
            {
                componentIndex++;
            }
            else if (starPattern >= 0)
            {
                // Backtrack: let the last '*' absorb one more character.
                patternIndex = starPattern + 1;
                componentIndex = ++starComponent;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    /// <summary>
    /// Consumes one pattern token (a literal, an escape, <c>?</c>, or a bracket class) and
    /// reports whether it matches <paramref name="candidate"/>. Advances
    /// <paramref name="patternIndex"/> past the token either way.
    /// </summary>
    private static bool MatchLiteral(string pattern, ref int patternIndex, char candidate)
    {
        var character = pattern[patternIndex];
        switch (character)
        {
            case '?':
                patternIndex++;
                return true;
            case '[':
                return MatchBracket(pattern, ref patternIndex, candidate);
            case '\\' when patternIndex + 1 < pattern.Length:
                patternIndex += 2;
                return pattern[patternIndex - 1] == candidate;
            default:
                patternIndex++;
                return character == candidate;
        }
    }

    private static bool MatchBracket(string pattern, ref int patternIndex, char candidate)
    {
        var closing = pattern.IndexOf(']', patternIndex + 1);
        if (closing < 0)
        {
            // An unterminated '[' is a literal bracket, as in every other glob dialect.
            patternIndex++;
            return candidate == '[';
        }

        var index = patternIndex + 1;
        var negated = pattern[index] is '!' or '^';
        if (negated)
        {
            index++;
        }

        var matched = false;
        while (index < closing)
        {
            if (index + 2 < closing && pattern[index + 1] == '-')
            {
                matched |= candidate >= pattern[index] && candidate <= pattern[index + 2];
                index += 3;
                continue;
            }

            matched |= pattern[index] == candidate;
            index++;
        }

        patternIndex = closing + 1;
        return matched != negated;
    }

    /// <summary>
    /// Normalises one .dockerignore line. An unanchored pattern with no slash matches at any
    /// depth, which is what the leading <c>**/</c> encodes.
    /// </summary>
    private static Rule? Parse(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return null;
        }

        var negate = line.StartsWith('!');
        var pattern = negate ? line[1..] : line;
        pattern = pattern.TrimEnd('/');
        var anchored = pattern.StartsWith('/');
        pattern = pattern.TrimStart('/');
        if (!anchored && !pattern.Contains('/'))
        {
            pattern = $"**/{pattern}";
        }

        return new Rule(pattern.Split('/'), negate);
    }

    private sealed record Rule(string[] Pattern, bool Negate);
}
