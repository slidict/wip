using System.Text.RegularExpressions;

namespace Wip.Security;

/// <summary>
/// One test user that ZAP logs in with standard form-based authentication, so the spider and
/// the active scan run as that user. The values are written into the plan inside the scanner
/// sandbox (<c>/tmp/wip-zap-plan.yaml</c>), the scanner process's argv and <c>--debug</c>
/// output: they are meant for test accounts of a sandboxed target, not for secrets.
/// </summary>
/// <param name="LoginUrl">The URL ZAP POSTs the login form to; it must be inside the scan's context.</param>
/// <param name="LoginBody">
/// The <c>application/x-www-form-urlencoded</c> body. ZAP replaces <c>{%username%}</c> and
/// <c>{%password%}</c> in it with the user's credentials.
/// </param>
/// <param name="Username">The ZAP user's username.</param>
/// <param name="Password">The ZAP user's password; may be empty.</param>
/// <param name="LoggedInRegex">A response pattern that shows the user is logged in.</param>
/// <param name="LoggedOutRegex">A response pattern that shows the user is logged out.</param>
internal sealed record ZapLogin(
    string LoginUrl,
    string LoginBody,
    string Username,
    string Password,
    string? LoggedInRegex = null,
    string? LoggedOutRegex = null)
{
    internal const string LoginUrlOption = "--zap-login-url";
    internal const string LoginBodyOption = "--zap-login-body";
    internal const string UsernameOption = "--zap-login-username";
    internal const string PasswordOption = "--zap-login-password";
    internal const string LoggedInRegexOption = "--zap-logged-in-regex";
    internal const string LoggedOutRegexOption = "--zap-logged-out-regex";

    /// <summary>The name of the ZAP user the spider and the active scan run as.</summary>
    internal const string UserName = "wip-user";

    public void Validate()
    {
        Require(LoginUrlOption, LoginUrl);
        Require(LoginBodyOption, LoginBody);
        Require(UsernameOption, Username);
        if (Password is null)
            throw new ConfigException($"{PasswordOption} must not be null; pass an empty value for no password");
        if (LoggedInRegex is null && LoggedOutRegex is null)
            throw new ConfigException(
                $"ZAP login requires {LoggedInRegexOption} or {LoggedOutRegexOption}, so ZAP can verify the session");
        RequirePattern(LoggedInRegexOption, LoggedInRegex);
        RequirePattern(LoggedOutRegexOption, LoggedOutRegex);
    }

    private static void Require(string option, string? value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ConfigException($"ZAP login requires {option}");
    }

    // An empty pattern matches every response, so the session check would always pass or always fail.
    // ZAP compiles the pattern with Java; .NET catches the malformed ones before any image pull.
    private static void RequirePattern(string option, string? pattern)
    {
        if (pattern is null) return;
        if (pattern.Length == 0)
            throw new ConfigException($"{option} must not be empty");
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException error)
        {
            throw new ConfigException($"{option} is not a valid regular expression: {error.Message}");
        }
    }
}
