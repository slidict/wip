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

    // Generous relative to the 3-second HTTP timeout below: if the helper crashed or was killed
    // before it could delete its lease, a later invocation reclaims it instead of update checks
    // going silent forever.
    private static readonly TimeSpan RefreshLeaseTimeout = TimeSpan.FromSeconds(30);

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

    internal static void RefreshCache(HttpMessageHandler? handler, string cachePath)
    {
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
        finally
        {
            TryDelete(LockPath(cachePath));
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
            if (!item.TryGetProperty("type", out var typeProperty) ||
                typeProperty.GetString() != "dir" ||
                !item.TryGetProperty("name", out var nameProperty) ||
                nameProperty.GetString() is not { } name ||
                !TryParseVersion(name, out var version, out _) ||
                latest is not null && version <= latest)
            {
                continue;
            }

            latest = version;
            latestText = name;
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
        var candidateIsNumeric = candidate.Length > 0 && candidate.All(char.IsAsciiDigit);
        var currentIsNumeric = current.Length > 0 && current.All(char.IsAsciiDigit);
        if (candidateIsNumeric && currentIsNumeric)
        {
            return long.Parse(candidate).CompareTo(long.Parse(current));
        }

        return candidateIsNumeric != currentIsNumeric
            ? candidateIsNumeric ? -1 : 1
            : string.CompareOrdinal(candidate, current);
    }

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

        var lockPath = TryAcquireRefreshLease(LockPath(CachePath()));
        if (lockPath is null)
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
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception)
        {
            TryDelete(lockPath);
        }
    }

    /// <summary>
    /// Atomically claims the right to refresh, so concurrent wip invocations against the same
    /// stale cache don't each spawn a helper and burn through GitHub's unauthenticated rate
    /// limit. Returns the lease path on success, so it can be released if the helper never
    /// starts.
    /// </summary>
    private static string? TryAcquireRefreshLease(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        if (TryCreateLease(lockPath))
        {
            return lockPath;
        }

        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) <= RefreshLeaseTimeout)
            {
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }

        TryDelete(lockPath);
        return TryCreateLease(lockPath) ? lockPath : null;
    }

    private static bool TryCreateLease(string lockPath)
    {
        try
        {
            using var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort cleanup; a leftover lease simply expires after RefreshLeaseTimeout.
        }
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
