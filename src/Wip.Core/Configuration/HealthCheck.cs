using System.Globalization;
using Wip.Yaml;

namespace Wip.Configuration;

/// <summary>
/// Parses a <c>healthcheck:</c> block — <c>dependencies.&lt;name&gt;.healthcheck:</c> under
/// <c>mode: container</c>, or a compose.yml service's own <c>healthcheck:</c> under
/// <c>mode: compose-native</c> — into the shape <c>wip up</c> polls against. Shared by both so
/// a dependency's readiness wait behaves identically regardless of which mode produced it.
/// </summary>
/// <remarks>
/// Real Compose accepts duration strings ("10s", "1m30s") for interval/timeout/start_period.
/// wip.yml's own numeric config fields (<c>sync.interval</c>, this one included) are plain
/// seconds instead — a compose.yml healthcheck already written with duration strings needs
/// those fields rewritten as numbers to be understood here.
/// </remarks>
public static class HealthCheck
{
    public const double DefaultInterval = 1;
    public const double DefaultTimeout = 1;
    public const long DefaultRetries = 3;
    public const double DefaultStartPeriod = 0;

    private static readonly string[] Keys = ["test", "interval", "timeout", "retries", "start_period"];

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
        result["interval"] = PositiveNumber(errorPrefix, "interval", mapping.GetValueOrDefault("interval"), DefaultInterval);
        result["timeout"] = PositiveNumber(errorPrefix, "timeout", mapping.GetValueOrDefault("timeout"), DefaultTimeout);
        result["retries"] = (long)PositiveNumber(errorPrefix, "retries", mapping.GetValueOrDefault("retries"), DefaultRetries);
        result["start_period"] =
            NonNegativeNumber(errorPrefix, "start_period", mapping.GetValueOrDefault("start_period"), DefaultStartPeriod);
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

    private static double PositiveNumber(string errorPrefix, string key, object? value, double defaultValue)
    {
        var seconds = ToDouble(errorPrefix, key, value, defaultValue);
        if (seconds <= 0)
        {
            throw new ConfigException($"{errorPrefix}.{key} must be a positive number");
        }

        return seconds;
    }

    private static double NonNegativeNumber(string errorPrefix, string key, object? value, double defaultValue)
    {
        var seconds = ToDouble(errorPrefix, key, value, defaultValue);
        if (seconds < 0)
        {
            throw new ConfigException($"{errorPrefix}.{key} must not be negative");
        }

        return seconds;
    }

    private static double ToDouble(string errorPrefix, string key, object? value, double defaultValue) => value switch
    {
        null => defaultValue,
        long or int or double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => throw new ConfigException($"{errorPrefix}.{key} must be a number of seconds"),
    };
}
