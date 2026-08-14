using System.Text;

namespace Wip.Platform;

/// <summary>
/// POSIX shell word splitting, as Ruby's <c>Shellwords.split</c> performs it.
/// </summary>
/// <remarks>
/// wip turns a <c>command:</c> string from wip.yml or compose.yml into an argv array
/// without ever handing it to a shell, so the splitting rules have to live here. The
/// BCL has no equivalent, and <c>Process</c>'s own Windows command-line parsing follows
/// different (MSVCRT) rules, so this is a direct port of Ruby's algorithm rather than a
/// reuse of anything built in. tests/golden/units/shellwords.json pins the behaviour.
/// </remarks>
public static class Shellwords
{
    /// <summary>Splits <paramref name="line"/> into words.</summary>
    /// <exception cref="ConfigException">An unbalanced single or double quote.</exception>
    public static IReadOnlyList<string> Split(string? line)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(line))
        {
            return words;
        }

        var field = new StringBuilder();
        var index = 0;

        while (true)
        {
            while (index < line.Length && IsShellSpace(line[index]))
            {
                index++;
            }

            if (index >= line.Length)
            {
                break;
            }

            index = ReadFragment(line, index, field);

            // Ruby's scanner only emits a word when whitespace (or end of input)
            // immediately follows the fragment, which is what lets `a"b"c` collapse
            // into a single word rather than three.
            if (index >= line.Length || IsShellSpace(line[index]))
            {
                words.Add(field.ToString());
                field.Clear();
            }
        }

        return words;
    }

    /// <summary>Joins <paramref name="words"/> so that splitting the result round-trips.</summary>
    public static string Join(IEnumerable<string> words) => string.Join(' ', words.Select(Escape));

    private static string Escape(string word)
    {
        if (word.Length == 0)
        {
            return "''";
        }

        var builder = new StringBuilder(word.Length);
        foreach (var character in word)
        {
            if (!char.IsAsciiLetterOrDigit(character) && !"_-.,:+/@\n".Contains(character))
            {
                builder.Append('\\');
            }

            // A backslash cannot escape a newline outside quotes; Ruby emits a quoted one.
            builder.Append(character == '\n' ? "'\n'" : character.ToString());
        }

        return builder.ToString();
    }

    private static int ReadFragment(string line, int index, StringBuilder field)
    {
        var character = line[index];
        return character switch
        {
            '\'' => ReadSingleQuoted(line, index, field),
            '"' => ReadDoubleQuoted(line, index, field),
            '\\' => ReadEscape(line, index, field),
            _ => ReadBareWord(line, index, field),
        };
    }

    private static int ReadBareWord(string line, int index, StringBuilder field)
    {
        var start = index;
        while (index < line.Length && !IsShellSpace(line[index]) && line[index] is not ('\\' or '\'' or '"'))
        {
            index++;
        }

        field.Append(line, start, index - start);
        return index;
    }

    private static int ReadSingleQuoted(string line, int index, StringBuilder field)
    {
        var closing = line.IndexOf('\'', index + 1);
        if (closing < 0)
        {
            throw Unmatched(line);
        }

        field.Append(line, index + 1, closing - index - 1);
        return closing + 1;
    }

    /// <summary>
    /// Inside double quotes a backslash escapes only <c>$</c>, <c>`</c>, <c>"</c>,
    /// <c>\</c>, and a newline. Every other backslash is a literal character, so
    /// <c>"\n"</c> stays a two-character sequence rather than becoming a line feed.
    /// </summary>
    private static int ReadDoubleQuoted(string line, int index, StringBuilder field)
    {
        index++;
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '"')
            {
                return index + 1;
            }

            if (character == '\\' && index + 1 < line.Length)
            {
                var escaped = line[index + 1];
                if (escaped is '$' or '`' or '"' or '\\' or '\n')
                {
                    field.Append(escaped);
                    index += 2;
                    continue;
                }

                field.Append(character).Append(escaped);
                index += 2;
                continue;
            }

            field.Append(character);
            index++;
        }

        throw Unmatched(line);
    }

    /// <summary>
    /// A backslash escapes the character after it. A trailing backslash has nothing to
    /// escape and stays literal, matching Ruby's optional-group behaviour.
    /// </summary>
    private static int ReadEscape(string line, int index, StringBuilder field)
    {
        if (index + 1 >= line.Length)
        {
            field.Append('\\');
            return index + 1;
        }

        field.Append(line[index + 1]);
        return index + 2;
    }

    private static ConfigException Unmatched(string line) =>
        new($"Unmatched quote: {Inspect(line)}");

    /// <summary>Renders a string the way Ruby's <c>String#inspect</c> would, for message parity.</summary>
    private static string Inspect(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            _ = character switch
            {
                '"' => builder.Append("\\\""),
                '\\' => builder.Append("\\\\"),
                '\n' => builder.Append("\\n"),
                '\t' => builder.Append("\\t"),
                '\r' => builder.Append("\\r"),
                _ => builder.Append(character),
            };
        }

        return builder.Append('"').ToString();
    }

    // Ruby's \s is ASCII-only here; char.IsWhiteSpace would also fold in Unicode
    // separators and split words the Ruby build kept together.
    private static bool IsShellSpace(char character) =>
        character is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';
}
