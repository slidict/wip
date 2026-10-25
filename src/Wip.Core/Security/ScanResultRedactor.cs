using System.Text;
using System.Text.RegularExpressions;

namespace Wip.Security;

/// <summary>Redacts normalized strings; raw reports and raw coverage remain separate artifacts.</summary>
internal static class ScanResultRedactor
{
    internal const string Marker = "[REDACTED]";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);
    private static readonly Regex Headers = new(
        """((?:^|[\s"'])(?:authorization|proxy-authorization|cookie|set-cookie)["']?\s*:\s*)[^\r\n]*""", Options, Deadline);
    private static readonly Regex Credentials = new(
        """(\b(?:Bearer|Basic)\s+)[a-z0-9._~+/=-]+""", Options, Deadline);
    private const string SecretNames = "access[_-]?token|refresh[_-]?token|id[_-]?token|token|api[_-]?key|client[_-]?secret|secret|password|passwd|session(?:[_-]?(?:id|token))?|auth|csrf(?:[_-]?token)?|xsrf(?:[_-]?token)?";
    // Bare assignments require '=' so a service alias such as auth:8080 is not a value.
    private static readonly Regex NamedSecrets = new(
        """(\b(?:""" + SecretNames + """)["']?\s*=\s*["']?)[^&\s"'<>;,}\]]+""", Options, Deadline);
    // Colon syntax is explicit only for a quoted object key or a key at a line boundary.
    // The key is captured so the alias exemption below can see which name it was.
    private static readonly Regex ColonSecrets = new(
        """((?:(?<line>(?:^|[\r\n])\s*(?<key>""" + SecretNames + """)\s*:\s*["']?)|["'](?:""" + SecretNames + """)["']\s*:\s*["']?))(?<value>[^&\s"'<>;,}\]]+)""", Options, Deadline);
    private static readonly Regex QueryValues = new(
        """((?:^|[?&\s])[^=&\s]+\s*=)([^&\s"'<>]+)""", Options, Deadline);
    private static readonly Regex UserInfo = new(
        """(https?://)[^/\s@]+@""", Options, Deadline);

    private static readonly Regex Locations = new(
        """(?<authority>(?:https?:)?//[^/\s?#"'<>]*)(?<path>/[^\s?#"'<>]*)?|(?<prefix>^|[\s"'(=])(?<path>/[^\s?#"'<>]*)""", Options, Deadline);

    private static readonly Regex SecretParameter = new(
        """(?:^|[^a-z0-9])(?:authorization|proxy-authorization|cookie|set-cookie|access[_-]?token|refresh[_-]?token|id[_-]?token|token|api[_-]?key|client[_-]?secret|secret|password|passwd|session(?:[_-]?(?:id|token))?|auth|csrf(?:[_-]?token)?|xsrf(?:[_-]?token)?)(?:$|[^a-z0-9])""", Options, Deadline);

    internal static string Redact(string text) => RedactCore(text, redactPaths: true);

    // Caller-supplied run metadata identifies the request. Preserve its exact path while
    // still removing explicit credentials and secret query values; crawled output uses Redact.
    internal static string RedactMetadata(string text) => RedactCore(text, redactPaths: false);

    private static string RedactCore(string text, bool redactPaths)
    {
        try
        {
            var redacted = Headers.Replace(text, match => match.Groups[1].Value + Marker);
            redacted = Credentials.Replace(redacted, match => match.Groups[1].Value + Marker);
            redacted = NamedSecrets.Replace(redacted, match => match.Groups[1].Value + Marker);
            redacted = ColonSecrets.Replace(redacted, match =>
                IsAliasAndPort(match) ? match.Value : match.Groups[1].Value + Marker);
            redacted = QueryValues.Replace(redacted, match =>
                match.Groups[1].Value + (LooksLikeToken(match.Groups[2].Value) ? Marker : match.Groups[2].Value));
            if (redactPaths)
                redacted = Locations.Replace(redacted, match =>
                    match.Groups["authority"].Value + match.Groups["prefix"].Value +
                    string.Join('/', match.Groups["path"].Value.Split('/').Select(segment => PathLooksLikeToken(segment) ? Marker : segment)));
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

    /// <summary>
    /// The one case a line-leading <c>name: digits</c> is not a secret: raw scanner output
    /// quotes a service alias and its port, as in <c>auth:8080 - crawled 12 URLs</c>.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. Exempting every numeric value let <c>password: 123456</c> and
    /// <c>token: 987654321</c> through, which are credentials however short they are. Of the
    /// names this redactor knows, only <c>auth</c> plausibly names a service, and a port is at
    /// most five digits -- so that pair is the exemption, and nothing else is.
    /// </remarks>
    private static bool IsAliasAndPort(Match match)
    {
        if (!match.Groups["line"].Success || !match.Groups["key"].Success) return false;
        if (!string.Equals(match.Groups["key"].Value, "auth", StringComparison.OrdinalIgnoreCase)) return false;
        var value = match.Groups["value"].Value;
        return value.Length is > 0 and <= 5 && value.All(char.IsAsciiDigit) &&
               int.TryParse(value, out var port) && port is > 0 and <= 65535;
    }

    private static bool PathLooksLikeToken(string value)
    {
        var decoded = Uri.UnescapeDataString(value);
        if (decoded.Length >= 16 && decoded.All(Uri.IsHexDigit)) return true;
        if (decoded.Length >= 24 && decoded.Any(char.IsAsciiLetter) && decoded.Any(char.IsAsciiDigit) &&
            decoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '~' or '+' or '/' or '='))
            return true;

        // A first byte of '{' is far too little evidence on its own: '{' is 0x7B, so any
        // base64 part starting "ey" decodes to it, and "eyes.min.js" or "eye.v2.png" is three
        // dot-separated parts of exactly that shape. Require the header to decode to a
        // complete JSON object naming one of the two fields JWS always carries, and require
        // the first two parts to be long enough to hold one.
        var parts = decoded.Split('.');
        if (parts.Length != 3 || parts[0].Length < 8 || parts[1].Length < 8 ||
            parts.Any(part => part.Length == 0 ||
                !part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            return false;
        try
        {
            var header = parts[0].Replace('-', '+').Replace('_', '/');
            header += new string('=', (4 - header.Length % 4) % 4);
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(header)).Trim();
            return text.StartsWith('{') && text.EndsWith('}') &&
                   (text.Contains("\"alg\"", StringComparison.Ordinal) || text.Contains("\"typ\"", StringComparison.Ordinal));
        }
        catch (FormatException) { return false; }
        catch (DecoderFallbackException) { return false; }
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
