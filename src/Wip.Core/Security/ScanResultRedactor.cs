using System.Text.RegularExpressions;

namespace Wip.Security;

/// <summary>Redacts normalized strings; raw reports and raw coverage remain separate artifacts.</summary>
internal static class ScanResultRedactor
{
    internal const string Marker = "[REDACTED]";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);
    private static readonly Regex Headers = new(
        """(\b(?:authorization|proxy-authorization|cookie|set-cookie)["']?\s*:\s*)[^\r\n]*""", Options, Deadline);
    private static readonly Regex Credentials = new(
        """(\b(?:Bearer|Basic)\s+)[a-z0-9._~+/=-]+""", Options, Deadline);
    private static readonly Regex NamedSecrets = new(
        """(\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|token|api[_-]?key|client[_-]?secret|secret|password|passwd|session(?:[_-]?(?:id|token))?|auth|csrf(?:[_-]?token)?|xsrf(?:[_-]?token)?)["']?\s*(?:=|:)\s*["']?)[^&\s"'<>;,}\]]+""", Options, Deadline);
    private static readonly Regex QueryValues = new(
        """((?:^|[?&\s])[^=&\s]+\s*=)([^&\s"'<>]+)""", Options, Deadline);
    private static readonly Regex UserInfo = new(
        """(https?://)[^/\s@]+@""", Options, Deadline);

    private static readonly Regex SecretParameter = new(
        """(?:^|[^a-z0-9])(?:authorization|proxy-authorization|cookie|set-cookie|access[_-]?token|refresh[_-]?token|id[_-]?token|token|api[_-]?key|client[_-]?secret|secret|password|passwd|session(?:[_-]?(?:id|token))?|auth|csrf(?:[_-]?token)?|xsrf(?:[_-]?token)?)(?:$|[^a-z0-9])""", Options, Deadline);

    internal static string Redact(string text)
    {
        try
        {
            var redacted = Headers.Replace(text, match => match.Groups[1].Value + Marker);
            redacted = Credentials.Replace(redacted, match => match.Groups[1].Value + Marker);
            redacted = NamedSecrets.Replace(redacted, match => match.Groups[1].Value + Marker);
            redacted = QueryValues.Replace(redacted, match =>
                match.Groups[1].Value + (LooksLikeToken(match.Groups[2].Value) ? Marker : match.Groups[2].Value));
            return UserInfo.Replace(redacted, match => match.Groups[1].Value + Marker + "@");
        }
        catch (RegexMatchTimeoutException)
        {
            // An untrusted field that exceeds the redaction budget must not leak unchanged.
            return Marker;
        }
    }

    internal static string RedactEvidence(string evidence, string? parameter)
    {
        if (evidence.Length == 0) return evidence;
        try
        {
            // ZAP often reports only the value; its parameter name supplies the context
            // that an Authorization header or token=value assignment would otherwise give.
            if ((parameter is not null && SecretParameter.IsMatch(parameter)) || LooksLikeToken(evidence.Trim()))
                return Marker;
            return Redact(evidence);
        }
        catch (RegexMatchTimeoutException) { return Marker; }
    }

    private static bool LooksLikeToken(string value)
    {
        var decoded = Uri.UnescapeDataString(value);
        if (decoded.Length >= 16 && decoded.All(Uri.IsHexDigit)) return true;
        if (decoded.Length < 20) return false;
        return decoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '~' or '+' or '/' or '=') &&
               (decoded.Length >= 32 || decoded.Any(char.IsAsciiDigit) || decoded.Contains('.'));
    }
}
