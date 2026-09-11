using System.Net.Http.Headers;
using System.Text.Json;

namespace Wip.Diagnostics;

/// <summary>Checks the public WinGet manifest index and advertises an available update.</summary>
public static class UpdateNotifier
{
    internal const string ManifestUrl =
        "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/s/Slidict/Wip";

    /// <summary>
    /// Best-effort by design: an unavailable network, a changed response, or a timeout must
    /// never stop (or change the exit code of) the command the user actually asked wip to run.
    /// </summary>
    public static void NotifyIfAvailable()
    {
        try
        {
            using var client = CreateClient();
            var latest = FindLatestVersion(client.GetStringAsync(ManifestUrl).GetAwaiter().GetResult());
            if (latest is not null && IsNewer(latest, WipVersion.Current))
            {
                Log.UpdateAvailable(WipVersion.Current, latest);
            }
        }
        catch (Exception)
        {
            // Update discovery is advisory. In particular, offline use remains fully usable.
        }
    }

    internal static string? FindLatestVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Version? latest = null;
        string? latestText = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameProperty) ||
                nameProperty.GetString() is not { } name ||
                !TryParseVersion(name, out var version) ||
                latest is not null && version <= latest)
            {
                continue;
            }

            latest = version;
            latestText = name;
        }

        return latestText;
    }

    internal static bool IsNewer(string candidate, string current) =>
        TryParseVersion(candidate, out var candidateVersion) &&
        TryParseVersion(current, out var currentVersion) &&
        candidateVersion > currentVersion;

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.TrimStart('v', 'V');
        var suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            normalized = normalized[..suffix];
        }

        return Version.TryParse(normalized, out version!);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("wip", WipVersion.Current));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
