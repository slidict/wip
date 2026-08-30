using System.Globalization;
using System.Text.RegularExpressions;
using Wip.Yaml;

namespace Wip.Configuration;

/// <summary>
/// Parses a <c>healthcheck:</c> block — <c>dependencies.&lt;name&gt;.healthcheck:</c> under
/// <c>mode: container</c>, or a compose.yml service's own <c>healthcheck:</c> under
/// <c>mode: compose-native</c> — into the shape <c>wip up</c> polls against. Shared by both so
/// a dependency's readiness wait behaves identically regardless of which mode produced it.
/// </summary>
/// <remarks>
/// <c>interval</c>/<c>timeout</c>/<c>start_period</c> accept either a plain number of seconds
/// (wip.yml's own convention, matching <c>sync.interval</c>) or a real Compose duration string
/// ("10s", "1m30s") — compose.yml healthchecks are near-universally written the latter way, so
/// mode: compose-native has to read them as they actually appear. <c>retries</c> stays a plain
/// count either way: Compose never accepts a duration there.
/// </remarks>
public static partial class HealthCheck
{
    public const double DefaultInterval = 1;
    public const double DefaultTimeout = 1;
    public const long DefaultRetries = 3;
    public const double DefaultStartPeriod = 0;

    private static readonly string[] Keys = ["test", "interval", "timeout", "retries", "start_period"];

    /// <summary>Matches one "&lt;number&gt;&lt;unit&gt;" segment of a Compose duration string.</summary>
    [GeneratedRegex(@"(\d+(?:\.\d+)?)(h|ms|us|m|s)")]
    private static partial Regex DurationSegment();

    /// <summary>
    /// Null input, and a <c>test: NONE</c> (or <c>test: ["NONE"]</c>) disabling one, both mean
    /// "no healthcheck" — the same as the key being absent entirely.
    /// </summary>
    public static OrderedDictionary<string, object?>? Normalize(string errorPrefix, object? value)
    {
        if (value is null)
        {
            return null;
        }

        var mapping = RubyValue.AsMapping(value) ?? throw new ConfigException($"{errorPrefix} must be a mapping");
        var unknown = mapping.Keys.Where(key => !Keys.Contains(key)).ToList();
        if (unknown.Count > 0)
        {
            throw new ConfigException($"{errorPrefix} has unsupported key(s): {string.Join(", ", unknown)}");
        }

        var test = NormalizeTest(errorPrefix, mapping.GetValueOrDefault("test"));
        if (test is null)
        {
            return null;
        }

        var result = RubyValue.NewMapping();
        result["test"] = test.Cast<object?>().ToList();
        result["interval"] = PositiveSeconds(errorPrefix, "interval", mapping.GetValueOrDefault("interval"), DefaultInterval);
        result["timeout"] = PositiveSeconds(errorPrefix, "timeout", mapping.GetValueOrDefault("timeout"), DefaultTimeout);
        result["retries"] = PositiveCount(errorPrefix, "retries", mapping.GetValueOrDefault("retries"), DefaultRetries);
        result["start_period"] =
            NonNegativeSeconds(errorPrefix, "start_period", mapping.GetValueOrDefault("start_period"), DefaultStartPeriod);
        return result;
    }

    /// <summary>
    /// Mirrors real Compose's three test shapes: a bare string is shell form, an array leads
    /// with <c>CMD</c> (exec form, run exactly as written) or <c>CMD-SHELL</c> (shell form),
    /// and <c>NONE</c> — spelled either way — disables the healthcheck.
    /// </summary>
    private static List<string>? NormalizeTest(string errorPrefix, object? value)
    {
        if (value is string text)
        {
            return text == "NONE" ? null : ["sh", "-c", text];
        }

        if (RubyValue.AsSequence(value) is not { } sequence || sequence.Count == 0)
        {
            throw new ConfigException($"{errorPrefix}.test must be a string or a non-empty array");
        }

        var items = sequence.Select(RubyValue.ToStringValue).ToList();
        return items[0] switch
        {
            "NONE" => null,
            "CMD" when items.Count > 1 => items[1..],
            "CMD" => throw new ConfigException($"{errorPrefix}.test: CMD needs at least one argument"),
            "CMD-SHELL" when items.Count == 2 => ["sh", "-c", items[1]],
            "CMD-SHELL" => throw new ConfigException($"{errorPrefix}.test: CMD-SHELL takes exactly one command"),
            _ => throw new ConfigException($"{errorPrefix}.test array must start with CMD, CMD-SHELL, or NONE"),
        };
    }

    private static double PositiveSeconds(string errorPrefix, string key, object? value, double defaultValue)
    {
        var seconds = ToSeconds(errorPrefix, key, value, defaultValue);
        if (seconds <= 0)
        {
            throw new ConfigException($"{errorPrefix}.{key} must be a positive number");
        }

        return seconds;
    }

    private static double NonNegativeSeconds(string errorPrefix, string key, object? value, double defaultValue)
    {
        var seconds = ToSeconds(errorPrefix, key, value, defaultValue);
        if (seconds < 0)
        {
            throw new ConfigException($"{errorPrefix}.{key} must not be negative");
        }

        return seconds;
    }

    private static double ToSeconds(string errorPrefix, string key, object? value, double defaultValue) => value switch
    {
        null => defaultValue,
        long or int or double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        string text => ParseDuration(errorPrefix, key, text),
        _ => throw new ConfigException($"{errorPrefix}.{key} must be a number of seconds or a duration string"),
    };

    /// <summary>
    /// A whole count of consecutive failures, never a duration: Compose itself never accepts
    /// one for <c>retries</c>, so "10s" here is a mistake to reject, not 10 seconds to guess at.
    /// </summary>
    private static long PositiveCount(string errorPrefix, string key, object? value, long defaultValue)
    {
        var count = value switch
        {
            null => defaultValue,
            long number => number,
            int number => number,
            double number => (long)number,
            _ => throw new ConfigException($"{errorPrefix}.{key} must be a whole number"),
        };

        if (count <= 0)
        {
            throw new ConfigException($"{errorPrefix}.{key} must be a positive number");
        }

        return count;
    }

    /// <summary>
    /// Compose's own duration syntax: one or more "&lt;number&gt;&lt;unit&gt;" segments —
    /// h/m/s/ms/us — concatenated with no separator (<c>1h30m</c>, <c>1m30s</c>). Rejects
    /// anything the segments don't fully account for, so a typo fails loudly instead of
    /// silently parsing a prefix and ignoring the rest.
    /// </summary>
    private static double ParseDuration(string errorPrefix, string key, string text)
    {
        var matches = DurationSegment().Matches(text);
        if (matches.Count == 0 || matches.Sum(match => match.Length) != text.Length)
        {
            throw new ConfigException(
                $"{errorPrefix}.{key} must be a number of seconds or a duration string (e.g. \"10s\", \"1m30s\")");
        }

        double seconds = 0;
        foreach (Match match in matches)
        {
            var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            seconds += match.Groups[2].Value switch
            {
                "h" => amount * 3600,
                "m" => amount * 60,
                "s" => amount,
                "ms" => amount / 1_000,
                "us" => amount / 1_000_000,
                _ => 0,
            };
        }

        return seconds;
    }
}
