using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Wip.Diagnostics;

/// <summary>Advertises WinGet updates without putting network I/O on a command's hot path.</summary>
public static class UpdateNotifier
{
    public const string RefreshArgument = "__refresh-winget-version";
    internal const string ManifestUrl =
        "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/s/Slidict/Wip";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    /// <summary>Reads the last result immediately, then refreshes stale data out of process.</summary>
    public static void NotifyIfAvailable()
    {
        try
        {
            var cached = ReadCache();
            if (cached.Latest is not null && IsNewer(cached.Latest, WipVersion.Current))
            {
                Log.UpdateAvailable(WipVersion.Current, cached.Latest);
            }

            if (DateTimeOffset.UtcNow - cached.CheckedAt >= RefreshInterval)
            {
                StartRefreshProcess();
            }
        }
        catch (Exception)
        {
            // Update discovery is advisory; even local cache/process failures are ignored.
        }
    }

    /// <summary>Executed only by the hidden detached helper process.</summary>
    public static void RefreshCache()
    {
        try
        {
            using var client = CreateClient();
            var latest = FindLatestVersion(client.GetStringAsync(ManifestUrl).GetAwaiter().GetResult());
            WriteCache(latest);
        }
        catch (Exception)
        {
            // Record the attempt to avoid slowing every invocation when the machine is offline.
            try
            {
                WriteCache(ReadCache().Latest);
            }
            catch (Exception)
            {
                // The cache itself may be unavailable; there is nothing advisory code can do.
            }
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
            if (!item.TryGetProperty("name", out var property) || property.GetString() is not { } name ||
                !TryParseVersion(name, out var version) || latest is not null && version <= latest)
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
        TryParseVersion(current, out var currentVersion) && candidateVersion > currentVersion;

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

    private static (DateTimeOffset CheckedAt, string? Latest) ReadCache()
    {
        var path = CachePath();
        if (!File.Exists(path))
        {
            return (DateTimeOffset.MinValue, null);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return (root.GetProperty("checkedAt").GetDateTimeOffset(), root.GetProperty("latest").GetString());
    }

    private static void WriteCache(string? latest)
    {
        var path = CachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        using (var stream = File.Create(temporary))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("checkedAt", DateTimeOffset.UtcNow);
            writer.WriteString("latest", latest);
            writer.WriteEndObject();
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string CachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wip", "update.json");

    private static void StartRefreshProcess()
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return;
        }

        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(RefreshArgument);
        Process.Start(start)?.Dispose();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("wip", WipVersion.Current));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
