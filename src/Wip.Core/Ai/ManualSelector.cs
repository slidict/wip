using System.Text.RegularExpressions;

namespace Wip.Ai;

/// <summary>
/// Picks which page(s) of the wiki manual (see <see cref="WikiManual"/>) are worth showing a
/// local AI model for a given question, instead of concatenating the whole ~270 KB manual into
/// every prompt — local models often run with a small context window, and most of the manual is
/// irrelevant to any one question anyway.
/// </summary>
/// <remarks>
/// Pure and I/O-free by design: no HTTP, no filesystem, so it is trivial to unit test against
/// fixed <see cref="ManualPage"/> values.
/// </remarks>
public static partial class ManualSelector
{
    /// <summary>Appended to a page truncated to fit the character budget; reserved space for
    /// this is deducted from the budget before slicing, so the result never runs past
    /// <c>maxCharacters</c> the way appending it afterward would.</summary>
    private const string TruncationMarker = "\n[truncated by wip]";

    /// <summary>
    /// <see cref="SelectRelevant"/>'s default character budget, exposed so a caller that adds
    /// its own formatting around the selected pages (headings, separators — see
    /// <c>CliContext.Join</c>) can clamp the final, formatted result to the same limit rather
    /// than letting that formatting overhead push it past what the budget was meant to enforce.
    /// </summary>
    public const int DefaultMaxCharacters = 12000;

    /// <summary>
    /// Common English function words that carry no page-selection signal on their own. Left
    /// unfiltered, a word like "the" or "how" occurs so often in ordinary prose that it can
    /// outscore the one real keyword in the question, dragging in unrelated pages (and pushing
    /// the actually relevant page's content past the character budget) — the exact way an
    /// otherwise-working query like "how do I disable the build cache?" picked
    /// <c>Shadow-Build-Context</c> and <c>Registry-Authentication</c> ahead of the real answer.
    /// Also includes "wip" itself: every single page in this manual is about wip, so — unlike
    /// an ordinary word — it can never help distinguish one page from another, and only dilutes
    /// whatever real signal a question also contains (e.g. "what does -d do on wip up" wants
    /// `wip-up`, not whichever page happens to say "wip" the most).
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "how", "what", "when", "where", "which", "who", "why", "the", "a", "an", "is", "are",
        "was", "were", "be", "been", "being", "do", "does", "did", "can", "could", "would",
        "should", "will", "shall", "to", "of", "in", "on", "at", "for", "and", "or", "but",
        "not", "no", "so", "if", "this", "that", "these", "those", "it", "its", "you", "your",
        "my", "me", "we", "us", "wip",
    };

    /// <summary>
    /// Pulls ASCII technical terms (flag names, command names) out of a question, even when the
    /// rest of the sentence is in another language — e.g. "ビルドキャッシュ使わないでbuildする
    /// には" yields just <c>build</c>, which is already enough to find the right page. Matches
    /// hyphenated flag names like <c>no-cache</c> as one token, and drops common function words
    /// (see <see cref="Stopwords"/>) that would otherwise swamp the real signal. The 2-character
    /// minimum matters on its own: `wip up` and `wip ps` are real commands, and a 3-character
    /// floor would silently make them unfindable. A single-letter short flag like <c>-d</c> or
    /// <c>-w</c> is matched as its own token too — it can't clear that 2-character floor on
    /// letters alone, so it needs the separate <c>-[A-Za-z]</c> alternative below rather than a
    /// lowered general floor, which would otherwise turn every lone letter into a keyword.
    /// </summary>
    public static IReadOnlyList<string> ExtractKeywords(string question) =>
        Keyword().Matches(question).Select(match => match.Value.ToLowerInvariant())
            .Where(keyword => !Stopwords.Contains(keyword))
            .Distinct()
            .ToList();

    /// <summary>
    /// Name-only scoring, for the *live* (uncached) path: which pages are worth an HTTP fetch
    /// before any content exists locally to search.
    /// </summary>
    public static IReadOnlyList<string> SelectCandidateNames(
        string question, IReadOnlyList<string> names, int limit = 8)
    {
        var keywords = ExtractKeywords(question);
        if (keywords.Count == 0)
        {
            return [];
        }

        return names
            .Select(name => (name, score: NameScore(name, keywords)))
            .Where(entry => entry.score > 0)
            .OrderByDescending(entry => entry.score)
            .Take(limit)
            .Select(entry => entry.name)
            .ToList();
    }

    /// <summary>
    /// Content-based scoring: ranks <paramref name="pages"/> by keyword occurrence count (with
    /// a bonus when the keyword is also in the page's name), then takes the top <paramref
    /// name="maxPages"/> and trims the combined content to <paramref name="maxCharacters"/> so
    /// the result stays within a small model's context budget.
    /// </summary>
    public static IReadOnlyList<ManualPage> SelectRelevant(
        string question, IReadOnlyList<ManualPage> pages, int maxPages = 3,
        int maxCharacters = DefaultMaxCharacters)
    {
        var keywords = ExtractKeywords(question);
        if (keywords.Count == 0)
        {
            return [];
        }

        var ranked = pages
            .Select(page => (page, score: NameScore(page.Name, keywords) + ContentScore(page.Content, keywords)))
            .Where(entry => entry.score > 0)
            .OrderByDescending(entry => entry.score)
            .Take(maxPages)
            .Select(entry => entry.page)
            .ToList();

        var trimmed = new List<ManualPage>();
        var budget = maxCharacters;
        foreach (var page in ranked)
        {
            if (budget <= 0)
            {
                break;
            }

            var fitted = Fit(page, budget);
            trimmed.Add(fitted);
            budget -= fitted.Content.Length;
        }

        return trimmed;
    }

    /// <summary>
    /// Fits <paramref name="page"/>'s content into <paramref name="budget"/> characters,
    /// guaranteeing the result never exceeds it — including the marker. When the marker itself
    /// wouldn't fit in what's left, it is dropped rather than appended anyway, since attaching a
    /// 19-character marker to a 5-character budget would blow straight past the limit it exists
    /// to enforce.
    /// </summary>
    private static ManualPage Fit(ManualPage page, int budget)
    {
        if (page.Content.Length <= budget)
        {
            return page;
        }

        return budget > TruncationMarker.Length
            ? page with { Content = page.Content[..(budget - TruncationMarker.Length)] + TruncationMarker }
            : page with { Content = page.Content[..budget] };
    }

    private static int NameScore(string name, IReadOnlyList<string> keywords) =>
        keywords.Count(keyword => CountWordStartOccurrences(name, keyword) > 0) * 5;

    private static int ContentScore(string content, IReadOnlyList<string> keywords) =>
        keywords.Sum(keyword => CountWordStartOccurrences(content, keyword));

    /// <summary>
    /// Occurrences of <paramref name="needle"/> in <paramref name="haystack"/> that start a
    /// word — the character before the match must be missing or non-alphanumeric — rather than
    /// merely appearing anywhere as a substring. Plain substring matching let a short command
    /// name like <c>up</c> or <c>ps</c> match inside "set<b>up</b>", "back<b>up</b>", or
    /// "ecli<b>ps</b>e", inflating unrelated pages ahead of the page that actually documents the
    /// command.
    /// </summary>
    /// <remarks>
    /// Anchored at the *start* only for an ordinary word of 3+ characters — requiring a
    /// boundary after the match too would stop "disable" from matching "disables", or "build"
    /// from matching "builds"/"building", ordinary English inflections this selector needs to
    /// keep finding. Two shapes are anchored at *both* ends instead, since neither has
    /// inflections worth preserving and each has its own way of hiding inside an unrelated,
    /// longer word as only a prefix: a short flag token (<paramref name="needle"/> starting
    /// with <c>-</c>, e.g. <c>-d</c>, which would otherwise match inside "-download" or
    /// "-debug"), and a 1-2 character command name (<c>up</c>, <c>ps</c>), which as a bare
    /// prefix would otherwise match inside ordinary words like "<b>up</b>date" or
    /// "<b>up</b>grade" that have nothing to do with the command.
    /// </remarks>
    private static int CountWordStartOccurrences(string haystack, string needle)
    {
        var requireEndBoundary = needle[0] == '-' || needle.Length <= 2;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var startsWord = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            var end = index + needle.Length;
            var endsWord = !requireEndBoundary || end == haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (startsWord && endsWord)
            {
                count++;
            }

            index += needle.Length;
        }

        return count;
    }

    [GeneratedRegex(@"-[A-Za-z](?![A-Za-z0-9-])|[A-Za-z][A-Za-z0-9\-]{1,}")]
    private static partial Regex Keyword();
}
