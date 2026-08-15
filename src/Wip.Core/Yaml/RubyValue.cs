using System.Globalization;

namespace Wip.Yaml;

/// <summary>
/// Helpers for reading the loosely typed trees <see cref="YamlLoader"/> produces.
/// </summary>
/// <remarks>
/// The conversions follow Ruby's, because the values flow straight into wslc command lines:
/// <c>ports: [3000]</c> has to reach the process as "3000", and <c>remove: no</c> has to be
/// falsy. Getting <c>to_s</c> wrong here would change generated commands, not just types.
/// </remarks>
public static class RubyValue
{
    /// <summary>Ruby's <c>to_s</c>: nil becomes "", booleans lower-case, floats keep a point.</summary>
    public static string ToStringValue(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "true" : "false",
        long number => number.ToString(CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        double number => FormatDouble(number),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Ruby truthiness: only nil and false are falsy.</summary>
    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        _ => true,
    };

    /// <summary>The string form, or null when it would be empty — Ruby's <c>presence</c>.</summary>
    public static string? Presence(object? value)
    {
        var text = ToStringValue(value);
        return text.Length == 0 ? null : text;
    }

    public static bool IsEmptyString(object? value) => ToStringValue(value).Length == 0;

    public static OrderedDictionary<string, object?>? AsMapping(object? value) =>
        value as OrderedDictionary<string, object?>;

    public static List<object?>? AsSequence(object? value) => value as List<object?>;

    /// <summary>
    /// Ruby's <c>Array(value)</c>: nil is no elements, an array is itself, anything else is
    /// a single element.
    /// </summary>
    public static List<object?> AsArray(object? value) => value switch
    {
        null => [],
        List<object?> list => list,
        _ => [value],
    };

    public static object? Dig(object? value, params string[] keys)
    {
        foreach (var key in keys)
        {
            var mapping = AsMapping(value);
            if (mapping is null || !mapping.TryGetValue(key, out value))
            {
                return null;
            }
        }

        return value;
    }

    public static OrderedDictionary<string, object?> NewMapping() => new(StringComparer.Ordinal);

    /// <summary>
    /// Ruby's <c>Hash#merge</c>: keys already present keep their position but take the new
    /// value; new keys are appended. Position matters because it becomes the order of the
    /// <c>-e</c> flags on the command line.
    /// </summary>
    public static OrderedDictionary<string, object?> Merge(
        OrderedDictionary<string, object?> left,
        OrderedDictionary<string, object?>? right)
    {
        var merged = new OrderedDictionary<string, object?>(left, StringComparer.Ordinal);
        if (right is null)
        {
            return merged;
        }

        foreach (var (key, value) in right)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static string FormatDouble(double number)
    {
        if (double.IsPositiveInfinity(number))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(number))
        {
            return "-Infinity";
        }

        if (double.IsNaN(number))
        {
            return "NaN";
        }

        // Ruby always renders a fractional part, so 1000.0 is "1000.0" and not "1000".
        var text = number.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : $"{text}.0";
    }
}
