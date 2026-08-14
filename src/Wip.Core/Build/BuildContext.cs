using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wip.Platform;

namespace Wip.Build;

/// <summary>
/// Filters build contexts for wslc, and keeps a WSL-hosted source tree in a fast, persistent
/// Windows-side cache.
/// </summary>
/// <remarks>
/// <para>
/// The Ruby build exposed this cache as an opt-in <c>shadow_context</c> key, because wip ran
/// inside a distribution and the mirror was only worth it for some projects. Running on the
/// Windows side inverts that: a project under <c>~/proj</c> reaches wip as a UNC path, so the
/// mirror is no longer a tuning knob but the thing that gives wslc a real local directory to
/// read. It is therefore automatic, and the config key is gone.
/// </para>
/// <para>
/// Walking a UNC tree is slow, so the manifest is built from the attributes the directory
/// enumeration already carries rather than by stat-ing each entry separately.
/// </para>
/// </remarks>
public sealed class BuildContext
{
    /// <summary>
    /// Bumped when the manifest's shape changes, so a stale cache is rebuilt once rather than
    /// compared against entries it cannot interpret. It changed on the port: Ruby recorded
    /// nanosecond mtimes, and .NET's are 100-nanosecond ticks.
    /// </summary>
    private const int ManifestVersion = 2;

    private readonly string root;
    private readonly DockerIgnore ignore;
    private readonly string cacheRoot;

    public BuildContext(string context, DockerIgnore? ignore = null, string? cacheRoot = null)
    {
        root = Path.GetFullPath(context);
        this.ignore = ignore ?? DockerIgnore.Load(Path.Combine(root, ".dockerignore"));
        this.cacheRoot = cacheRoot ?? DefaultCacheRoot();
    }

    /// <summary>
    /// Whether the upcoming stage will use the persistent cache rather than staging in place
    /// or under a temporary directory.
    /// </summary>
    public bool UsesCache => WslPath.IsWslPath(root);

    public static string DefaultCacheRoot() => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "wip",
        "contexts");

    /// <summary>
    /// Prepares the context and invokes <paramref name="action"/> with the directory wslc
    /// should build from. <paramref name="onProgress"/> fires before copying and after each
    /// file, as (count, total), so a caller can report progress even while copying one large
    /// file.
    /// </summary>
    public void Stage(Action<string> action, Action<int, int>? onProgress = null)
    {
        if (UsesCache)
        {
            StageToCache(action, onProgress);
            return;
        }

        if (ignore.IsEmpty)
        {
            action(root);
            return;
        }

        var temporary = Directory.CreateTempSubdirectory("wip-build-context-");
        try
        {
            CopyIncludedFiles(temporary.FullName, onProgress);
            action(temporary.FullName);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Keeps one stable cache directory per source path. Its manifest lives beside — rather
    /// than inside — the context, so it is never sent to wslc.
    /// </summary>
    private void StageToCache(Action<string> action, Action<int, int>? onProgress)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
        var cache = Path.Combine(cacheRoot, key);
        var context = Path.Combine(cache, "context");
        Directory.CreateDirectory(cache);

        // Holding the lock for the whole call keeps the cache immutable until wslc has
        // finished reading it, so two concurrent builds cannot interleave.
        using var lockFile = new FileStream(
            Path.Combine(cache, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Synchronize(context, Path.Combine(cache, "manifest.json"), onProgress);
        action(context);
    }

    private void Synchronize(string context, string manifestPath, Action<int, int>? onProgress)
    {
        var current = IncludedFiles().ToDictionary(entry => entry.RelativePath, entry => entry.Fingerprint,
            StringComparer.Ordinal);
        var previous = PreviousManifest(context, manifestPath);

        var changed = current.Where(pair => !previous.TryGetValue(pair.Key, out var old) || old != pair.Value)
            .Select(pair => pair.Key).ToList();
        var removed = previous.Keys.Where(key => !current.ContainsKey(key)).ToList();

        var total = changed.Count + removed.Count;
        onProgress?.Invoke(0, total);
        var done = 0;

        foreach (var entry in removed)
        {
            var target = Path.Combine(context, entry);
            DeleteEntry(target);
            PruneEmptyParents(Path.GetDirectoryName(target), context);
            onProgress?.Invoke(++done, total);
        }

        foreach (var entry in changed)
        {
            CopyEntryAtomically(Path.Combine(root, entry), Path.Combine(context, entry));
            onProgress?.Invoke(++done, total);
        }

        Directory.CreateDirectory(context);
        WriteManifest(manifestPath, current);
    }

    /// <summary>
    /// A context we cannot describe is a context we cannot update incrementally: with no
    /// readable manifest there is no way to tell which entries are stale, so it is discarded
    /// and rebuilt rather than left holding deleted or newly ignored files.
    /// </summary>
    private static Dictionary<string, string> PreviousManifest(string context, string manifestPath)
    {
        if (!Directory.Exists(context))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var manifest = ReadManifest(manifestPath);
        if (manifest is not null)
        {
            return manifest;
        }

        Directory.Delete(context, recursive: true);
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static Dictionary<string, string>? ReadManifest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) ||
                version.GetInt32() != ManifestVersion ||
                !root.TryGetProperty("entries", out var entries))
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateObject())
            {
                result[entry.Name] = entry.Value.GetString() ?? string.Empty;
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteManifest(string path, Dictionary<string, string> entries)
    {
        var temporary = $"{path}.tmp-{System.Environment.ProcessId}";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", ManifestVersion);
                writer.WriteStartObject("entries");
                foreach (var (key, value) in entries)
                {
                    writer.WriteString(key, value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Copies via a temporary name and renames into place, so an interrupted update leaves
    /// the previous copy rather than no copy at all.
    /// </summary>
    private static void CopyEntryAtomically(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(target)!,
            $".{Path.GetFileName(target)}.wip-{System.Environment.ProcessId}");

        try
        {
            DeleteEntry(temporary);

            // Links are copied as links. Following one here could pull arbitrary host files
            // from outside the build context — a private key, say — into the staged copy and
            // hand them to the image build.
            var info = new FileInfo(source);
            if (info.LinkTarget is { } linkTarget)
            {
                File.CreateSymbolicLink(temporary, linkTarget);
            }
            else
            {
                File.Copy(source, temporary, overwrite: true);
            }

            DeleteEntry(target);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            DeleteEntry(temporary);
        }
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            File.Delete(path);
        }
    }

    private static void PruneEmptyParents(string? directory, string stopAt)
    {
        var boundary = Path.TrimEndingDirectorySeparator(stopAt);
        while (directory is not null &&
               Path.TrimEndingDirectorySeparator(directory) != boundary &&
               directory.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               Directory.Exists(directory) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private void CopyIncludedFiles(string destination, Action<int, int>? onProgress)
    {
        var files = IncludedFiles().ToList();
        onProgress?.Invoke(0, files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            CopyEntryAtomically(
                Path.Combine(root, files[index].RelativePath),
                Path.Combine(destination, files[index].RelativePath));
            onProgress?.Invoke(index + 1, files.Count);
        }
    }

    /// <summary>
    /// Walks the tree by hand rather than globbing it all up front, so an ignored directory —
    /// node_modules, vendor/bundle, a multi-gigabyte storage/ — is never descended into just
    /// to be thrown away afterwards.
    /// </summary>
    private IEnumerable<(string RelativePath, string Fingerprint)> IncludedFiles()
    {
        var results = new List<(string, string)>();
        Walk(root, string.Empty, results);
        return results;
    }

    private void Walk(string directory, string prefix, List<(string, string)> results)
    {
        // Enumerating as FileSystemInfo carries size, timestamps, and attributes along with
        // the name, so building the manifest costs one directory read rather than one stat
        // per file — which is what makes this bearable over a UNC path.
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            var relative = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            var ignored = ignore.Ignored(relative);
            var isLink = entry.LinkTarget is not null;

            if (!isLink && entry is DirectoryInfo child)
            {
                // Only prune when no later negated rule could re-include something beneath.
                if (ignored && ignore.Prunable(relative))
                {
                    continue;
                }

                Walk(child.FullName, relative, results);
                continue;
            }

            if (!ignored)
            {
                results.Add((relative, Fingerprint(entry)));
            }
        }
    }

    private static string Fingerprint(FileSystemInfo entry)
    {
        if (entry.LinkTarget is { } target)
        {
            return $"link:{target}";
        }

        var length = entry is FileInfo file ? file.Length : 0;
        return $"file:{length}:{entry.LastWriteTimeUtc.Ticks}";
    }
}
