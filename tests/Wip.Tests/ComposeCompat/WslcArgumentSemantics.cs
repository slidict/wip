using System.Text.Json.Nodes;

namespace Wip.Tests.ComposeCompat;

/// <summary>
/// How <c>wslc run</c> reads the <c>-p</c> and <c>-v</c> strings wip hands it. Under
/// <c>mode: compose-native</c> wip forwards compose.yml's <c>ports:</c> and <c>volumes:</c>
/// entries verbatim, so this — not wip's own parser — is what decides what those entries end
/// up meaning.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>PublishPort::Parse</c> (src/windows/wslc/services/ContainerModel.cpp) and
/// <c>ParseDockerVolumeString</c> (src/windows/common/MountSpecParsing.cpp) at the upstream
/// commit pinned in tests/compose-compat/upstream/PINNED.md. It is a reading aid for the
/// comparison, never a source of behaviour: nothing under src/ calls it.
/// </para>
/// <para>
/// The CLI parser is markedly more permissive than <c>wslc compose</c>'s own — ranges,
/// <c>/udp</c>, a bind address, a bare container port — which is the whole reason it is
/// modelled separately.
/// </para>
/// </remarks>
internal static class WslcArgumentSemantics
{
    /// <summary>
    /// wslc resolves an unqualified <c>-p</c> against the session's configured binding
    /// address rather than a fixed loopback literal, so the model records the indirection
    /// instead of guessing a value.
    /// </summary>
    internal const string SessionDefaultBindingAddress = "wslc-session-default";

    internal sealed class SpecException(string message) : Exception(message);

    /// <summary>ContainerModel.cpp: protocol suffix, then container port, then host part.</summary>
    internal static JsonObject ParsePort(string value)
    {
        var protocol = "tcp";
        var portPart = value;
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            portPart = value[..slash];
            protocol = value[(slash + 1)..];
            if (protocol is not ("tcp" or "udp"))
            {
                throw new SpecException(
                    "Invalid protocol specified in port mapping. Only 'tcp' and 'udp' are supported.");
            }
        }

        string? hostPart = null;
        var colon = portPart.LastIndexOf(':');
        var containerPort = colon >= 0 ? ParseRange(portPart[(colon + 1)..]) : ParseRange(portPart);
        if (colon >= 0)
        {
            hostPart = portPart[..colon];
        }

        string? hostIp = null;
        var hostPort = (Start: 0, End: 0);
        if (hostPart is not null)
        {
            var hostColon = hostPart.LastIndexOf(':');
            if (hostColon >= 0)
            {
                hostIp = Unbracket(hostPart[..hostColon]);
                var rest = hostPart[(hostColon + 1)..];
                if (rest.Length > 0)
                {
                    hostPort = ParseRange(rest);
                }
            }
            else
            {
                hostPort = ParseRange(hostPart);
            }
        }

        // Validate(): an ephemeral (0) host port is exempt; anything else has to be a real
        // port and cover exactly as many ports as the container side does.
        var ephemeral = hostPort is { Start: 0, End: 0 };
        if (!ephemeral && Count(hostPort) != Count(containerPort))
        {
            throw new SpecException("Host port range must match the container port range.");
        }

        return new JsonObject
        {
            // Left null when the spec names no address: which address an unqualified
            // publish lands on is a whole-implementation default, tracked as port_binding_default.
            ["host_ip"] = hostIp,
            ["host_port"] = ephemeral ? "ephemeral" : Describe(hostPort),
            ["container_port"] = Describe(containerPort),
            ["protocol"] = protocol,
        };
    }

    /// <summary>
    /// MountSpecParsing.cpp: an <c>ro</c>/<c>rw</c> tail, then the rightmost remaining colon
    /// splits source from target. A source matching Docker's named-volume shape is a volume;
    /// everything else is a bind whose relative source wslc resolves against its own current
    /// directory — the process's, not compose.yml's.
    /// </summary>
    internal static JsonObject ParseVolume(string value)
    {
        const string usage = "expected [host-path|volume-name]:container-path[:ro|rw]";
        var lastColon = value.LastIndexOf(':');
        if (lastColon < 0)
        {
            throw new SpecException($"Invalid volume specification '{value}': {usage}");
        }

        var splitColon = lastColon;
        var readOnly = false;
        var lastToken = value[(lastColon + 1)..];
        var hasMode = lastToken is "ro" or "rw";
        if (hasMode)
        {
            readOnly = lastToken == "ro";
            if (lastColon == 0)
            {
                throw new SpecException($"Invalid volume specification '{value}': {usage}");
            }

            splitColon = value.LastIndexOf(':', lastColon - 1);
            if (splitColon < 0)
            {
                throw new SpecException($"Invalid volume specification '{value}': {usage}");
            }
        }

        var targetEnd = hasMode ? lastColon : value.Length;
        var target = value[(splitColon + 1)..targetEnd];
        if (target.Length == 0)
        {
            throw new SpecException($"Volume '{value}' has an empty container path: {usage}");
        }

        if (target[0] != '/')
        {
            throw new SpecException($"Volume '{value}' container path must be absolute: {usage}");
        }

        var source = value[..splitColon];
        if (source.Length == 0)
        {
            throw new SpecException($"Volume '{value}' has an empty host path: {usage}");
        }

        return IsNamedVolume(source)
            ? new JsonObject
            {
                ["type"] = "volume",
                ["source"] = source,
                ["source_base"] = null,
                ["target"] = target,
                ["read_only"] = readOnly,
            }
            : new JsonObject
            {
                // Left as written: wip forwards the token untouched, and GetFullPathNameW
                // resolves it inside wslc against wip's own current directory -- which is not
                // a property of the fixture, so only the anchor is recorded.
                ["type"] = "bind",
                ["source"] = source,
                ["source_base"] = HostPathBase.AnchorOf(source) switch
                {
                    HostPathBase.Anchor.Absolute => "absolute",
                    HostPathBase.Anchor.RootRelative => "process-cwd-drive",
                    _ => "process-cwd",
                },
                ["target"] = target,
                ["read_only"] = readOnly,
            };
    }

    /// <summary>Docker's own named-volume shape, which wslc reuses: <c>^[a-zA-Z0-9][a-zA-Z0-9_.-]{1,}$</c>.</summary>
    private static bool IsNamedVolume(string source) =>
        source.Length >= 2 &&
        char.IsAsciiLetterOrDigit(source[0]) &&
        source.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');

    private static (int Start, int End) ParseRange(string text)
    {
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            return (ParsePort(text[..dash], text), ParsePort(text[(dash + 1)..], text));
        }

        var port = ParsePort(text, text);
        return (port, port);
    }

    // IsValidPort() is 1..65535, so wslc's CLI rejects the '0:8080' that its compose parser
    // accepts as an ephemeral host port.
    private static int ParsePort(string text, string original)
    {
        if (text.Length == 0 || !text.All(char.IsAsciiDigit) ||
            !int.TryParse(text, out var port) || port is < 1 or > 65535)
        {
            throw new SpecException($"Invalid port specified in port mapping: '{original}'.");
        }

        return port;
    }

    private static int Count((int Start, int End) range) =>
        range.End >= range.Start ? range.End - range.Start + 1 : 0;

    private static string Describe((int Start, int End) range) =>
        range.Start == range.End ? range.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{range.Start}-{range.End}";

    private static string Unbracket(string address) =>
        address.Length >= 2 && address[0] == '[' && address[^1] == ']' ? address[1..^1] : address;
}
