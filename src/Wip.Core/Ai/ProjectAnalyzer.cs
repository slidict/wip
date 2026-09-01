using System.Text;
using System.Text.RegularExpressions;

namespace Wip.Ai;

/// <summary>Collects a small, allow-listed project snapshot suitable for an AI prompt.</summary>
public sealed partial class ProjectAnalyzer
{
    public const int MaxFileCharacters = 64 * 1024;
    public const int MaxTotalCharacters = 256 * 1024;
    public const int MaxFiles = 24;

    private static readonly string[] ExactNames =
    [
        "README", "README.md", "README.txt", "Gemfile", "Gemfile.lock", "package.json",
        "Procfile", "compose.yml", "compose.yaml", "docker-compose.yml", "docker-compose.yaml",
        "Dockerfile", ".ruby-version", ".node-version", "go.mod", "Cargo.toml", "pyproject.toml",
        "requirements.txt", "global.json", "Directory.Build.props",
    ];

    private readonly string directory;

    /// <summary>Markers that identify a credential by its own shape, wherever they appear.</summary>
    private static readonly string[] SecretMarkers =
    [
        "-----BEGIN PRIVATE KEY-----", "-----BEGIN RSA PRIVATE KEY-----", "AKIA",
        "ghp_", "github_pat_", "sk-",
    ];

    public ProjectAnalyzer(string? directory = null) =>
        this.directory = Path.GetFullPath(directory ?? Directory.GetCurrentDirectory());

    public ProjectSnapshot Analyze()
    {
        var files = new List<ProjectFile>();
        var total = 0;
        foreach (var name in ExactNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || files.Count >= MaxFiles || total >= MaxTotalCharacters ||
                File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var remaining = Math.Min(MaxFileCharacters, MaxTotalCharacters - total);
            var content = ReadBounded(path, remaining);
            if (ContainsPossibleSecret(content))
            {
                continue;
            }

            total += content.Length;
            files.Add(new ProjectFile(name, content));
        }

        return new ProjectSnapshot(directory, files);
    }

    /// <summary>Conservatively identifies files that should be excluded rather than rewritten.</summary>
    public static bool ContainsPossibleSecret(string content) =>
        SecretMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
        SecretAssignment().IsMatch(content);

    /// <summary>
    /// Assignment-shaped credentials, matched with the whitespace real files actually contain:
    /// <c>API_KEY = "..."</c> and <c>password : "..."</c> name a secret exactly as plainly as
    /// <c>api_key=</c> does, so keying the check on the unspaced form alone would have let the
    /// file straight into the prompt. The names stay deliberately broad and the whole file is
    /// dropped on a match, since a false positive only costs the model some context.
    /// </summary>
    [GeneratedRegex(@"(api[_-]?key|secret|token|password|passwd)[ \t]*[:=]", RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    private static string ReadBounded(string path, int limit)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[limit + 1];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        var result = new string(buffer, 0, Math.Min(count, limit));
        return count > limit ? result + "\n[truncated by wip]" : result;
    }
}

public sealed record ProjectSnapshot(string Root, IReadOnlyList<ProjectFile> Files)
{
    public string ToPromptText()
    {
        var text = new StringBuilder();
        foreach (var file in Files)
        {
            text.AppendLine($"--- {file.RelativePath} ---");
            text.AppendLine(file.Content);
        }
        return text.ToString();
    }
}

public sealed record ProjectFile(string RelativePath, string Content);
