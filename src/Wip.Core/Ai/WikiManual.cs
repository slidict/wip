using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Wip.Ai;

/// <summary>One page of the <c>slidict/wip.wiki</c> manual, keyed by its wiki slug (e.g.
/// <c>wip-build</c>), the same name it is cached under as <c>&lt;Name&gt;.md</c>.</summary>
public readonly record struct ManualPage(string Name, string Content);

/// <summary>
/// Reads the <c>slidict/wip.wiki</c> manual over plain HTTP — GitHub serves every wiki page's
/// raw Markdown at <c>raw.githubusercontent.com/wiki/&lt;owner&gt;/&lt;repo&gt;/&lt;Page&gt;.md</c>,
/// so no <c>git clone</c> (and no dependency on a Windows-side <c>git</c>) is needed to reach
/// it. <c>_Sidebar.md</c> doubles as the page index: it is a plain <c>[Title](Slug)</c> link
/// list naming every other page.
/// </summary>
/// <remarks>
/// Mirrors <see cref="LocalAiProvider"/>'s shape: an injectable <see cref="HttpMessageHandler"/>
/// for tests, and network failures surfaced as <see cref="WipException"/> rather than a raw
/// <see cref="HttpRequestException"/>.
/// </remarks>
public sealed partial class WikiManual
{
    private const string Owner = "slidict";
    private const string Repository = "wip";
    private const string RawBaseUrl = "https://raw.githubusercontent.com/wiki";
    private const string SidebarPageName = "_Sidebar";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    private readonly HttpMessageHandler? handler;

    public WikiManual(HttpMessageHandler? handler = null) => this.handler = handler;

    /// <summary>Where <c>wip manual</c> writes pages to, and where <see cref="LoadCache"/> reads
    /// them back from: <c>%LocalAppData%\wip\manual</c>, the same
    /// <c>%LocalAppData%\wip\&lt;name&gt;</c> shape as <c>BuildContext.DefaultCacheRoot()</c>.</summary>
    public static string DefaultCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wip", "manual");

    /// <summary>Whether the wiki's raw-content host is reachable, so callers can fall back to
    /// "no manual" instead of hanging or throwing when there is no network. Host/port are
    /// overridable so tests can point this at a local listener instead of the real internet.</summary>
    public static bool IsReachable(string? host = null, int port = 443)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host ?? "raw.githubusercontent.com", port).Wait(ProbeTimeout);
        }
        catch (Exception exception) when (exception is AggregateException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads whatever <c>*.md</c> pages are already sitting in <paramref
    /// name="cacheDirectory"/> — empty when <c>wip manual</c> has never been run.</summary>
    public static IReadOnlyList<ManualPage> LoadCache(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return [];
        }

        return Directory.GetFiles(cacheDirectory, "*.md")
            .Select(path => new ManualPage(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path)))
            .ToList();
    }

    /// <summary>Fetches <c>_Sidebar.md</c> and extracts every page name it links to.</summary>
    public IReadOnlyList<string> FetchPageNames()
    {
        var sidebar = FetchRaw(SidebarPageName);
        return SidebarLink().Matches(sidebar).Select(match => match.Groups[1].Value).Distinct().ToList();
    }

    /// <summary>Fetches each named page, silently skipping one that fails (a renamed or removed
    /// page in a stale <c>_Sidebar.md</c> reference should not take the whole batch down).</summary>
    public IReadOnlyList<ManualPage> FetchPages(IReadOnlyList<string> names)
    {
        var pages = new List<ManualPage>();
        foreach (var name in names)
        {
            try
            {
                pages.Add(new ManualPage(name, FetchRaw(name)));
            }
            catch (WipException)
            {
                // Best-effort: one missing/renamed page should not sink the rest.
            }
        }

        return pages;
    }

    /// <summary>Downloads every page named in <c>_Sidebar.md</c> into <paramref
    /// name="cacheDirectory"/>, overwriting whatever was cached before. Returns the number of
    /// pages written.</summary>
    public int Download(string cacheDirectory)
    {
        var names = FetchPageNames();
        var pages = FetchPages(names);

        Directory.CreateDirectory(cacheDirectory);
        foreach (var page in pages)
        {
            File.WriteAllText(Path.Combine(cacheDirectory, $"{page.Name}.md"), page.Content);
        }

        return pages.Count;
    }

    private string FetchRaw(string pageName)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);

        HttpResponseMessage response;
        try
        {
            response = client.GetAsync($"{RawBaseUrl}/{Owner}/{Repository}/{pageName}.md").GetAwaiter().GetResult();
        }
        catch (HttpRequestException exception)
        {
            throw new WipException($"Could not reach the wip wiki fetching '{pageName}'", exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new WipException($"Timed out fetching '{pageName}' from the wip wiki", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new WipException($"wip wiki returned {(int)response.StatusCode} fetching '{pageName}'");
            }

            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
    }

    [GeneratedRegex(@"\[[^\]]+\]\(([^)\s]+)\)")]
    private static partial Regex SidebarLink();
}
