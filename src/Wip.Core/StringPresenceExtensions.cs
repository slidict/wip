namespace Wip;

/// <summary>Ruby's <c>presence</c> for plain strings: empty becomes null.</summary>
internal static class StringPresenceExtensions
{
    internal static string? Presence(this string? value) => string.IsNullOrEmpty(value) ? null : value;
}
