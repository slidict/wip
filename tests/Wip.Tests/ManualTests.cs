using Wip.Ai;

namespace Wip.Tests;

/// <summary>Covers <see cref="WikiManual"/> (reading/fetching the wiki manual) and <see
/// cref="ManualSelector"/> (deciding which page(s) are relevant to a question) independently of
/// <c>CliContext.HelpAi</c>, which wires the two together.</summary>
public class ManualTests
{
    [Fact]
    public void LoadCacheReturnsEmptyForAMissingDirectory()
    {
        Assert.Empty(WikiManual.LoadCache(Path.Combine(Path.GetTempPath(), "wip-manual-does-not-exist")));
    }

    [Fact]
    public void LoadCacheReadsEveryMarkdownFileByItsNameWithoutExtension()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "wip-build.md"), "# wip build\n--no-cache disables it");
        File.WriteAllText(Path.Combine(directory.Path, "wip-up.md"), "# wip up");

        var pages = WikiManual.LoadCache(directory.Path);

        Assert.Equal(2, pages.Count);
        Assert.Contains(pages, page => page.Name == "wip-build" && page.Content.Contains("--no-cache"));
    }

    [Fact]
    public void FetchPageNamesParsesEveryLinkOutOfTheSidebar()
    {
        var handler = new StubWikiHandler(new Dictionary<string, string>
        {
            ["_Sidebar"] = """
                **[Home](Home)**
                - [wip build](wip-build)
                - [wip up](wip-up)
                """,
        });

        var names = new WikiManual(handler).FetchPageNames();

        Assert.Equal(["Home", "wip-build", "wip-up"], names);
    }

    /// <summary>A link target becomes both a URL segment and a cache filename (see <see
    /// cref="WikiManual.Download"/>), so a fragment/query suffix or a traversal-shaped target
    /// must never survive into the returned name list.</summary>
    [Fact]
    public void FetchPageNamesStripsFragmentsAndRejectsUnsafeTargets()
    {
        var handler = new StubWikiHandler(new Dictionary<string, string>
        {
            ["_Sidebar"] = """
                - [Build flags](wip-build#flags)
                - [Search](wip-build?query=1)
                - [Traversal](../../etc/passwd)
                - [wip up](wip-up)
                """,
        });

        var names = new WikiManual(handler).FetchPageNames();

        Assert.Equal(["wip-build", "wip-up"], names);
    }

    /// <summary>
    /// `HttpClient(handler)` disposes <paramref name="handler"/> by default once the client is
    /// torn down; since <see cref="WikiManual.FetchPages"/> and <see cref="WikiManual.Download"/>
    /// call <c>FetchRaw</c> in a loop on one injected handler, that default would break every
    /// fetch after the first against a real (non-test-double) handler.
    /// </summary>
    [Fact]
    public void FetchingMultiplePagesDoesNotDisposeAnInjectedHandler()
    {
        var handler = new DisposeTrackingWikiHandler(new Dictionary<string, string>
        {
            ["_Sidebar"] = "- [wip build](wip-build)\n- [wip up](wip-up)",
            ["wip-build"] = "# wip build",
            ["wip-up"] = "# wip up",
        });

        var wiki = new WikiManual(handler);
        wiki.FetchPages(wiki.FetchPageNames());

        Assert.False(handler.WasDisposed);
    }

    [Fact]
    public void FetchPagesSkipsAPageThatFailsInsteadOfThrowing()
    {
        var handler = new StubWikiHandler(new Dictionary<string, string>
        {
            ["wip-build"] = "# wip build",
        });

        var pages = new WikiManual(handler).FetchPages(["wip-build", "missing-page"]);

        var page = Assert.Single(pages);
        Assert.Equal("wip-build", page.Name);
    }

    [Fact]
    public void DownloadWritesEveryFetchedPageToTheCacheDirectory()
    {
        using var directory = new TemporaryDirectory();
        var handler = new StubWikiHandler(new Dictionary<string, string>
        {
            ["_Sidebar"] = "- [wip build](wip-build)\n- [wip up](wip-up)",
            ["wip-build"] = "# wip build\n--no-cache disables it",
            ["wip-up"] = "# wip up",
        });

        var count = new WikiManual(handler).Download(directory.Path);

        Assert.Equal(2, count);
        Assert.Equal("# wip build\n--no-cache disables it", File.ReadAllText(Path.Combine(directory.Path, "wip-build.md")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "wip-up.md")));
    }

    [Fact]
    public void IsReachableFindsAListeningHostAndRejectsAClosedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(WikiManual.IsReachable("127.0.0.1", port));
        }
        finally
        {
            listener.Stop();
        }

        Assert.False(WikiManual.IsReachable("127.0.0.1", 1));
    }

    /// <summary>Reproduces issue #135's own repro question: "build" is the only ASCII keyword
    /// in it, and matching it against page names/content must still be enough to surface the
    /// page documenting `--no-cache`.</summary>
    [Fact]
    public void SelectRelevantFindsTheBuildPageFromTheIssuesJapaneseReproQuestion()
    {
        var pages = new[]
        {
            new ManualPage("wip-build", "# wip build\n`--no-cache` disables the build cache."),
            new ManualPage("wip-up", "# wip up\nStarts the configured stack."),
        };

        var selected = ManualSelector.SelectRelevant("ビルドキャッシュ使わないでbuildするにはどうするんだ？", pages);

        var page = Assert.Single(selected);
        Assert.Equal("wip-build", page.Name);
    }

    /// <summary>
    /// Regression for a real failure: "how do I disable the build cache?" ranked
    /// `Shadow-Build-Context` (heavy on ordinary prose) ahead of `wip-build` (the actual
    /// answer) before stopwords were filtered, because "the" and "how" occur so often in plain
    /// English that they outweighed the one real signal, "build" — and the diluted excerpt made
    /// the local model answer "not covered" for a question it should have been able to answer.
    /// </summary>
    [Fact]
    public void SelectRelevantIgnoresStopwordsSoTheRealAnswerOutranksGenericProse()
    {
        var pages = new[]
        {
            new ManualPage("wip-build", "# wip build\n`--no-cache` disables the build cache."),
            new ManualPage("Shadow-Build-Context", string.Concat(Enumerable.Repeat("the quick brown fox jumps. ", 50))),
        };

        var selected = ManualSelector.SelectRelevant("how do I disable the build cache?", pages, maxPages: 1);

        var page = Assert.Single(selected);
        Assert.Equal("wip-build", page.Name);
    }

    [Fact]
    public void ExtractKeywordsDropsCommonEnglishFunctionWords()
    {
        var keywords = ManualSelector.ExtractKeywords("how do I disable the build cache?");

        Assert.Equal(["disable", "build", "cache"], keywords);
    }

    [Fact]
    public void SelectRelevantReturnsNothingWhenNoKeywordMatchesAnyPage()
    {
        var pages = new[] { new ManualPage("wip-build", "# wip build") };

        Assert.Empty(ManualSelector.SelectRelevant("何もヒットしない質問です", pages));
    }

    [Fact]
    public void SelectRelevantTrimsToTheCharacterBudget()
    {
        var pages = new[] { new ManualPage("wip-build", "build " + new string('x', 100)) };

        var selected = ManualSelector.SelectRelevant("build", pages, maxCharacters: 20);

        var page = Assert.Single(selected);
        Assert.Equal(20 + "\n[truncated by wip]".Length, page.Content.Length);
        Assert.EndsWith("[truncated by wip]", page.Content);
    }

    [Fact]
    public void SelectCandidateNamesMatchesPageNamesContainingAKeyword()
    {
        var names = new[] { "wip-build", "Compose-Build", "wip-up", "Dockerignore" };

        var candidates = ManualSelector.SelectCandidateNames("how do I build without cache", names);

        Assert.Contains("wip-build", candidates);
        Assert.Contains("Compose-Build", candidates);
        Assert.DoesNotContain("wip-up", candidates);
        Assert.DoesNotContain("Dockerignore", candidates);
    }

    [Fact]
    public void SelectCandidateNamesReturnsNothingForAQuestionWithNoAsciiKeywords()
    {
        Assert.Empty(ManualSelector.SelectCandidateNames("これは英数字を含まない質問", ["wip-build"]));
    }

    /// <summary>
    /// `up` and `ps` are real, two-letter wip commands. A 3-character keyword floor would make
    /// them permanently unmatchable — this pins the 2-character minimum that fixed it.
    /// </summary>
    [Fact]
    public void ExtractKeywordsIncludesTwoLetterCommandNames()
    {
        Assert.Equal(["up"], ManualSelector.ExtractKeywords("up"));
        Assert.Equal(["ps"], ManualSelector.ExtractKeywords("ps"));
    }

    [Fact]
    public void SelectCandidateNamesMatchesTwoLetterCommandNames()
    {
        var names = new[] { "wip-up", "wip-ps", "wip-build" };

        Assert.Contains("wip-up", ManualSelector.SelectCandidateNames("how do I start it back up", names));
        Assert.Contains("wip-ps", ManualSelector.SelectCandidateNames("wip ps command", names));
    }

    private class StubWikiHandler(IReadOnlyDictionary<string, string> pagesByName) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            if (!pagesByName.TryGetValue(name, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content),
            });
        }
    }

    private sealed class DisposeTrackingWikiHandler(IReadOnlyDictionary<string, string> pagesByName)
        : StubWikiHandler(pagesByName)
    {
        internal bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("wip-manual-test-").FullName;
        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
