using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace Wip.Yaml;

/// <summary>
/// Decides what an unquoted YAML scalar means, matching Ruby's Psych.
/// </summary>
/// <remarks>
/// <para>
/// This is not cosmetic typing. <c>restart: no</c> resolves to the boolean <c>false</c>
/// rather than the string "no" — both <see cref="Configuration.Config"/> and
/// <see cref="Compose.ComposeFile"/> special-case that — and <c>PORT: 3000</c> is an
/// integer where <c>PORT: "3000"</c> is a string. YamlDotNet's representation model hands
/// back the raw text and leaves resolution to the caller, which is exactly what makes it
/// reflection-free and AOT-safe, so the rules live here.
/// </para>
/// <para>
/// tests/golden/units/yaml_scalars.json records Psych's real answers. Three of them are
/// deliberately not reproduced, because supporting them would cost more than the
/// pathological configs they'd serve: a sexagesimal scalar (<c>12:30</c>, which Psych reads
/// as the integer 45000), and date and timestamp scalars (<c>2024-01-02</c>). All three are
/// read as plain strings here. Note that dates and timestamps were never usable anyway —
/// wip loads YAML with an empty permitted-classes list, so Psych raised on them.
/// </para>
/// </remarks>
public static partial class YamlScalarResolver
{
    public static object? Resolve(string value, ScalarStyle style)
    {
        // Any quoting or block style forces a string: "3000" and 'no' stay text.
        if (style != ScalarStyle.Plain)
        {
            return value;
        }

        if (NullPattern().IsMatch(value))
        {
            return null;
        }

        if (TruePattern().IsMatch(value))
        {
            return true;
        }

        if (FalsePattern().IsMatch(value))
        {
            return false;
        }

        return ResolveNumber(value) ?? value;
    }

    private static object? ResolveNumber(string value)
    {
        if (IntegerPattern().IsMatch(value))
        {
            return ParseInteger(value);
        }

        if (!FloatPattern().IsMatch(value))
        {
            return null;
        }

        var digits = Strip(value);
        if (digits.EndsWith("inf", StringComparison.OrdinalIgnoreCase))
        {
            return digits.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (digits.EndsWith("nan", StringComparison.OrdinalIgnoreCase))
        {
            return double.NaN;
        }

        // Psych accepts a bare leading or trailing dot ('.5', '1.'); double.Parse does too
        // with these flags, so the pattern above is the only gate.
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static object? ParseInteger(string value)
    {
        var text = Strip(value);
        var negative = text.StartsWith('-');
        if (text.StartsWith('-') || text.StartsWith('+'))
        {
            text = text[1..];
        }

        var (digits, radix) = text switch
        {
            ['0', 'b' or 'B', .. var rest] => (rest, 2),
            ['0', 'x' or 'X', .. var rest] => (rest, 16),
            ['0', .. var rest] when rest.Length > 0 => (rest, 8),
            _ => (text, 10),
        };

        try
        {
            var magnitude = Convert.ToInt64(digits, radix);
            return negative ? -magnitude : magnitude;
        }
        catch (Exception exception) when (exception is OverflowException or FormatException or ArgumentException)
        {
            // Ruby integers are arbitrary precision; anything that will not fit in a long
            // stays a string rather than silently wrapping.
            return null;
        }
    }

    // Psych treats ',' and '_' alike as digit separators.
    private static string Strip(string value) => value.Replace("_", "").Replace(",", "");

    [GeneratedRegex(@"\A(~|null|Null|NULL|)\z")]
    private static partial Regex NullPattern();

    [GeneratedRegex(@"\A(yes|Yes|YES|true|True|TRUE|on|On|ON)\z")]
    private static partial Regex TruePattern();

    [GeneratedRegex(@"\A(no|No|NO|false|False|FALSE|off|Off|OFF)\z")]
    private static partial Regex FalsePattern();

    // Base 2, legacy octal, base 10, and base 16. A leading zero followed by a non-octal
    // digit ('09') matches nothing here and stays a string, exactly as Psych leaves it.
    [GeneratedRegex(@"\A([-+]?0b[0-1_,]+|[-+]?0[0-7_,]+|[-+]?(0|[1-9](?:[0-9]|,[0-9]|_[0-9])*)|[-+]?0x[0-9a-fA-F_,]+)\z")]
    private static partial Regex IntegerPattern();

    // Psych requires an explicit sign in the exponent, which is why '1.0e3' is a string
    // while '1.0e+3' is a float.
    [GeneratedRegex(@"\A([-+]?([0-9][0-9_,]*)?\.[0-9]*([eE][-+][0-9]+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))\z")]
    private static partial Regex FloatPattern();
}
