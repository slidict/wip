using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wip.Diagnostics;
using Wip.Execution;

namespace Wip.Security;

/// <summary>Fetches verified rules during preparation; run-time lookup never invokes git.</summary>
internal sealed class RulesFetcher(
    string cacheDirectory,
    Func<IReadOnlyList<string>, TimeSpan, (int Code, string Output)>? run = null)
{
    internal const string TreeDirectoryName = "tree";
    // Version 1 allowed host-native EOL conversion. Refuse those entries rather than
    // reuse checkouts whose bytes may depend on the host that prepared them.
    private const int CacheVersion = 2;

    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(5);

    internal string Fetch(PinnedRules rules)
    {
        rules.Validate("rules");
        var cache = Path.GetFullPath(cacheDirectory);
        var destination = Path.Combine(cache, rules.Commit);
        if (Directory.Exists(destination) || File.Exists(destination)) return RequireStaged(rules);
        var temporary = Path.Combine(cache, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var tree = Path.Combine(temporary, TreeDirectoryName);
            var hooks = Path.Combine(temporary, "hooks");
            var configuration = Path.Combine(temporary, "git-config");
            Directory.CreateDirectory(tree);
            Directory.CreateDirectory(hooks);
            File.WriteAllText(configuration, "");
            Git(tree, hooks, configuration, ["init", "--quiet", "--object-format=sha1", "--template=" + hooks], LocalTimeout);
            Git(tree, hooks, configuration,
                ["fetch", "--depth", "1", "--no-tags", "--no-recurse-submodules", "--", rules.RepositoryUrl, rules.Commit], FetchTimeout);
            var resolved = Git(tree, hooks, configuration, ["rev-parse", "--verify", "FETCH_HEAD"], LocalTimeout).Trim();
            if (!string.Equals(resolved, rules.Commit, StringComparison.Ordinal))
                throw new WipException("Pinned rules commit verification failed; no rules were staged");
            Git(tree, hooks, configuration, ["checkout", "--quiet", "--detach", "--force", rules.Commit, "--"], LocalTimeout);
            DeleteOwnedTree(Path.Combine(tree, ".git"));
            Directory.Delete(hooks);
            File.Delete(configuration);
            var fingerprint = Fingerprint(tree);
            using (var stream = new FileStream(Path.Combine(temporary, "verified.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteNumber("version", CacheVersion);
                writer.WriteString("commit", rules.Commit);
                writer.WriteString("treeSha256", fingerprint.Hash);
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            try { Directory.Move(temporary, destination); }
            catch (IOException) when (Directory.Exists(destination))
            {
                // Another preparation may have published the same immutable pin first.
                return RequireStaged(rules);
            }
            return RequireStaged(rules);
        }
        catch (IOException error) { throw new WipException("Pinned rules could not be staged in the host cache", error); }
        catch (UnauthorizedAccessException error) { throw new WipException("Pinned rules cache is inaccessible", error); }
        finally
        {
            // Do not replace a failed fetch, verification or publication with a cleanup error.
            try { if (Directory.Exists(temporary)) DeleteOwnedTree(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal string RequireStaged(PinnedRules rules) => RequireStagedTree(rules).Path;

    internal (string Path, int FileCount) RequireStagedTree(PinnedRules rules)
    {
        rules.Validate("rules");
        var entry = Path.Combine(Path.GetFullPath(cacheDirectory), rules.Commit);
        var tree = Path.Combine(entry, TreeDirectoryName);
        var marker = Path.Combine(entry, "verified.json");
        try
        {
            if (!Directory.Exists(entry) || !Directory.Exists(tree) || !File.Exists(marker))
                throw new WipException("Pinned rules have not been verified and staged; run preparation before scanning");
            RejectLink(entry);
            RejectLink(marker);
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != CacheVersion ||
                !root.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.String || commit.GetString() != rules.Commit ||
                !root.TryGetProperty("treeSha256", out var digest) || digest.ValueKind != JsonValueKind.String)
                throw new WipException("Pinned rules cache does not match its verified content; remove the cache entry and prepare again");
            var fingerprint = Fingerprint(tree);
            if (!string.Equals(digest.GetString(), fingerprint.Hash, StringComparison.Ordinal))
                throw new WipException("Pinned rules cache does not match its verified content; remove the cache entry and prepare again");
            // The commit name is the verified immutable pin, so reuse needs no network.
            // The marker/fingerprint reject partial publication and later content changes.
            // This is a user-owned host cache, not a boundary against a user rewriting both.
            return (tree, fingerprint.FileCount);
        }
        catch (JsonException error) { throw new WipException("Pinned rules cache verification marker is malformed", error); }
        catch (IOException error) { throw new WipException("Pinned rules cache could not be read", error); }
        catch (UnauthorizedAccessException error) { throw new WipException("Pinned rules cache is inaccessible", error); }
    }

    private string Git(string tree, string hooks, string configuration, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        // autocrlf=false alone does not stop attribute-driven native EOL conversion.
        // Measured blob: a LF b LF c LF; native checkout: a CRLF b CRLF c CRLF;
        // with core.eol=lf: a LF b LF c LF. Otherwise fingerprints depend on the host.
        string[] command = ["git", "-c", "core.hooksPath=" + hooks, "-c", "core.autocrlf=false", "-c", "core.eol=lf",
            "-c", "credential.helper=", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-C", tree, .. arguments];
        (int Code, string Output) result;
        try { result = run is not null ? run(command, timeout) : RunGit(command, timeout, tree, hooks, configuration); }
        catch (Win32Exception error) { throw GitRequired(error); }
        catch (FileNotFoundException error) { throw GitRequired(error); }
        catch (UnauthorizedAccessException error) { throw GitRequired(error); }
        if (result.Code == 127) throw GitRequired();
        if (result.Code != 0)
            throw new WipException($"Pinned rules git {arguments[0]} failed (exit {result.Code}); no unverified rules will be used");
        return result.Output;
    }

    private static WipException GitRequired(Exception? error = null) => error is null
        ? new WipException("A runnable git installation is required to fetch and verify pinned rules")
        : new WipException("A runnable git installation is required to fetch and verify pinned rules", error);

    private static (int Code, string Output) RunGit(IReadOnlyList<string> command, TimeSpan timeout, string tree, string hooks, string configuration)
    {
        var gitDirectory = Path.Combine(tree, ".git");
        // Isolate checkout from host hooks, filters, config injection and repository context.
        var environment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_SYSTEM"] = configuration,
            ["GIT_CONFIG_GLOBAL"] = configuration, ["GIT_CONFIG_COUNT"] = "0", ["GIT_CONFIG_PARAMETERS"] = "",
            ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_TEMPLATE_DIR"] = hooks, ["GIT_ATTR_NOSYSTEM"] = "1",
            ["GIT_DIR"] = gitDirectory, ["GIT_WORK_TREE"] = tree, ["GIT_COMMON_DIR"] = gitDirectory,
            ["GIT_INDEX_FILE"] = Path.Combine(gitDirectory, "index"), ["GIT_OBJECT_DIRECTORY"] = Path.Combine(gitDirectory, "objects"),
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "", ["GIT_NAMESPACE"] = ""
        };
        using var output = new StringWriter();
        var runner = new CommandRunner(new ErrorInterpreter("linux/amd64"), output, TextWriter.Null);
        var code = runner.Run(command, environment: environment, timeout: timeout);
        return (code, output.ToString());
    }

    private static (string Hash, int FileCount) Fingerprint(string tree)
    {
        RejectLink(tree);
        var files = new List<string>();
        CollectFiles(tree, files);
        if (files.Count == 0) throw new WipException("Pinned rules checkout is empty; a scan with no rules is refused");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files.Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(tree, path).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative + '\0'));
            using var content = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(content));
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), files.Count);
    }

    private static void CollectFiles(string directory, List<string> files)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(entry);
            if (Directory.Exists(entry)) CollectFiles(entry, files);
            else files.Add(entry);
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new WipException("Pinned rules cannot contain symbolic links or reparse points; host staging must not escape its verified tree");
    }

    private static void DeleteOwnedTree(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path);
            else File.Delete(path);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteOwnedTree(child);
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            Directory.Delete(path);
        }
        else
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }
}
