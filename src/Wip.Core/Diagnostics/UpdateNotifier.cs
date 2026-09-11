using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Wip.Diagnostics;

/// <summary>Advertises WinGet updates without putting network I/O on a command's hot path.</summary>
public static class UpdateNotifier
{
    private const string RefreshHelperEnvironmentVariable = "WIP_UPDATE_REFRESH_HELPER";
    internal const string ManifestUrl =
        "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/s/Slidict/Wip?per_page=100";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// True when this process was launched by <see cref="StartRefreshProcess"/> to run
    /// <see cref="RefreshCache()"/> and exit, rather than a normal wip invocation. Gated by an
    /// environment variable set only on that child process, not by argv, so an ordinary command
    /// the user happens to type can never be mistaken for the helper.
    /// </summary>
    public static bool IsRefreshHelperInvocation =>
        Environment.GetEnvironmentVariable(RefreshHelperEnvironmentVariable) == "1";

    /// <summary>Reads the last result immediately, then refreshes stale data out of process.</summary>
    public static void NotifyIfAvailable()
    {
        try
        {
            var cached = ReadCache(CachePath());
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
    public static void RefreshCache() => RefreshCache(handler: null, CachePath());

    /// <summary>
    /// Holds an exclusive OS-level lock on the lease file for as long as the refresh takes.
    /// Concurrent wip invocations against the same stale cache can each spawn a helper, but only
    /// the one that wins the lock actually calls GitHub; the rest see the lock already taken and
    /// exit immediately. Unlike a lease file whose mere existence is the marker, a held lock
    /// needs no staleness timeout: if this process crashes, Windows releases the lock the moment
    /// it exits, so a later invocation acquires it right away instead of waiting out a timeout.
    /// </summary>
    internal static void RefreshCache(HttpMessageHandler? handler, string cachePath)
    {
        var lockPath = LockPath(cachePath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        FileStream lease;
        try
        {
            lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            // Another refresh is already in flight for this cache.
            return;
        }

        using (lease)
        {
            // Winning the lock only means no refresh is in flight right now; a different
            // helper could have already refreshed and released it between when this one was
            // spawned and when it got the lock. Re-check freshness before spending another
            // GitHub request on a cache that is no longer stale.
            if (DateTimeOffset.UtcNow - ReadCache(cachePath).CheckedAt < RefreshInterval)
            {
                return;
            }

            try
            {
                using var client = CreateClient(handler);
                var latest = FindLatestVersion(client.GetStringAsync(ManifestUrl).GetAwaiter().GetResult());
                WriteCache(latest, cachePath);
            }
            catch (Exception)
            {
                // Record the attempt to avoid slowing every invocation when the machine is offline.
                try
                {
                    WriteCache(ReadCache(cachePath).Latest, cachePath);
                }
                catch (Exception)
                {
                    // The cache itself may be unavailable; there is nothing advisory code can do.
                }
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

        // Compared pairwise with IsNewer (full SemVer precedence, prerelease included) rather
        // than by parsed Version alone, so the winner does not depend on manifest entry order:
        // a release must beat a same-numbered prerelease regardless of which one is enumerated
        // first.
        string? latestText = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeProperty) ||
                typeProperty.GetString() != "dir" ||
                !item.TryGetProperty("name", out var nameProperty) ||
                nameProperty.GetString() is not { } name ||
                !TryParseVersion(name, out _, out _))
            {
                continue;
            }

            if (latestText is null || IsNewer(name, latestText))
            {
                latestText = name;
            }
        }
        return latestText;
    }

    internal static bool IsNewer(string candidate, string current)
    {
        if (!TryParseVersion(candidate, out var candidateVersion, out var candidatePrerelease) ||
            !TryParseVersion(current, out var currentVersion, out var currentPrerelease))
        {
            return false;
        }

        return candidateVersion != currentVersion
            ? candidateVersion > currentVersion
            : ComparePrerelease(candidatePrerelease, currentPrerelease) > 0;
    }

    /// <summary>
    /// Follows SemVer 2.0's precedence rules (spec item 11): a release with no prerelease tag
    /// always outranks a prerelease of the same numeric version; two prereleases compare their
    /// dot-separated identifiers left to right, where a numeric identifier always ranks below an
    /// alphanumeric one, and running out of identifiers first ranks lower.
    /// </summary>
    private static int ComparePrerelease(string? candidate, string? current)
    {
        if (candidate == current)
        {
            return 0;
        }

        if (candidate is null || current is null)
        {
            return candidate is null ? 1 : -1;
        }

        var candidateIds = candidate.Split('.');
        var currentIds = current.Split('.');
        var sharedLength = Math.Min(candidateIds.Length, currentIds.Length);
        for (var i = 0; i < sharedLength; i++)
        {
            var comparison = CompareIdentifier(candidateIds[i], currentIds[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return candidateIds.Length.CompareTo(currentIds.Length);
    }

    private static int CompareIdentifier(string candidate, string current)
    {
        var candidateIsNumeric = IsValidNumericIdentifier(candidate);
        var currentIsNumeric = IsValidNumericIdentifier(current);
        if (candidateIsNumeric && currentIsNumeric)
        {
            // SemVer numeric identifiers have no length limit, so comparing them as integers
            // could overflow; a longer run of digits is always the larger number, and
            // same-length digit strings order the same numerically and lexically. Leading
            // zeroes (rejected by IsValidNumericIdentifier below) would otherwise break the
            // length comparison: "00" is not shorter than "0" despite being numerically equal.
            return candidate.Length != current.Length
                ? candidate.Length.CompareTo(current.Length)
                : string.CompareOrdinal(candidate, current);
        }

        return candidateIsNumeric != currentIsNumeric
            ? candidateIsNumeric ? -1 : 1
            : string.CompareOrdinal(candidate, current);
    }

    /// <summary>SemVer numeric identifiers disallow leading zeroes, so "0" is numeric but
    /// "00"/"01" are not -- comparing them as numbers would treat those as equal or misordered.
    /// A digit run rejected here still compares fine as plain text via CompareOrdinal above.</summary>
    private static bool IsValidNumericIdentifier(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) && (value.Length == 1 || value[0] != '0');

    private static bool TryParseVersion(string value, out Version version, out string? prerelease)
    {
        var normalized = value.TrimStart('v', 'V');
        var buildSuffix = normalized.IndexOf('+');
        if (buildSuffix >= 0)
        {
            normalized = normalized[..buildSuffix];
        }

        var prereleaseSuffix = normalized.IndexOf('-');
        if (prereleaseSuffix >= 0)
        {
            prerelease = normalized[(prereleaseSuffix + 1)..];
            normalized = normalized[..prereleaseSuffix];
        }
        else
        {
            prerelease = null;
        }

        return Version.TryParse(normalized, out version!);
    }

    internal static (DateTimeOffset CheckedAt, string? Latest) ReadCache(string cachePath)
    {
        if (!File.Exists(cachePath))
        {
            return (DateTimeOffset.MinValue, null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(cachePath));
            var root = document.RootElement;
            return (root.GetProperty("checkedAt").GetDateTimeOffset(), root.GetProperty("latest").GetString());
        }
        catch (Exception)
        {
            // A truncated or hand-edited cache file must not permanently disable update checks;
            // treat it the same as a missing one so the next refresh recreates it.
            return (DateTimeOffset.MinValue, null);
        }
    }

    internal static void WriteCache(string? latest, string cachePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporary = $"{cachePath}.{Environment.ProcessId}.tmp";
        using (var stream = File.Create(temporary))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("checkedAt", DateTimeOffset.UtcNow);
            writer.WriteString("latest", latest);
            writer.WriteEndObject();
        }

        File.Move(temporary, cachePath, overwrite: true);
    }

    private static string CachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wip", "update.json");

    private static string LockPath(string cachePath) => $"{cachePath}.lock";

    private static void StartRefreshProcess()
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return;
        }

        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };

        // Under `dotnet run`/`dotnet path/to/wip.dll`, ProcessPath is the dotnet host itself, not
        // wip: without the assembly path dotnet has nothing to execute and the helper never
        // refreshes the cache. args[0] is the managed entry point dotnet was given, which is
        // exactly that path (and reading it, unlike Assembly.Location, is safe under AOT/single
        // file too).
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            Environment.GetCommandLineArgs() is [{ Length: > 0 } assemblyPath, ..])
        {
            start.ArgumentList.Add(assemblyPath);
        }

        start.EnvironmentVariables[RefreshHelperEnvironmentVariable] = "1";

        // RefreshCache() itself claims the cross-process lock (see its remarks), so a redundant
        // helper spawned here for the same stale cache just loses that race and exits at once.
        Process.Start(start)?.Dispose();
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(3);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("wip", WipVersion.Current));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
