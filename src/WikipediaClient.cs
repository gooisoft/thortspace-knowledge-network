using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// Fetches topics from the MediaWiki API (en.wikipedia.org/w/api.php) — no scraping, no bot-blocking:
/// one "extracts" request for the article as plain text, plus paginated "links" requests for its
/// outbound article links (the raw material of the cluster graph). Polite by construction: a real
/// User-Agent and a small delay between calls.
/// </summary>
public sealed class WikipediaClient
{
    private const int MaxLinkPages = 5;              // 500 links/request -> up to 2500 candidate links
    private const int PolitenessDelayMs = 150;
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ThortspaceKnowledgeNetwork/1.0 (+https://github.com/gooisoft/thortspace-knowledge-network)");
        return c;
    }

    /// <summary>Fetch one page: canonical title, plain-text extract, outbound links. Throws if no article.</summary>
    public async Task<WikiPage> FetchAsync(string topic)
    {
        var (title, extract) = await FetchExtractAsync(topic);
        var links = await FetchLinksAsync(title);
        var url = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'));
        return new WikiPage(title, url, extract, links);
    }

    private async Task<(string title, string extract)> FetchExtractAsync(string topic)
    {
        var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&prop=extracts&explaintext=1" +
                  $"&exsectionformat=wiki&redirects=1&titles={Uri.EscapeDataString(topic)}";
        using var doc = JsonDocument.Parse(await GetAsync(url));
        var page = FirstPage(doc);
        if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("extract", out var ex))
            throw new InvalidOperationException($"No Wikipedia article found for \"{topic}\".");
        var title = page.TryGetProperty("title", out var t) ? t.GetString() ?? topic : topic;
        return (title, ex.GetString() ?? "");
    }

    /// <summary>Outbound article links (namespace 0 only), following the API's continuation cursor.</summary>
    private async Task<List<string>> FetchLinksAsync(string title)
    {
        var links = new List<string>();
        string? cont = null;
        for (var i = 0; i < MaxLinkPages; i++)
        {
            var url = "https://en.wikipedia.org/w/api.php?action=query&format=json&prop=links&plnamespace=0" +
                      $"&pllimit=max&redirects=1&titles={Uri.EscapeDataString(title)}" +
                      (cont != null ? $"&plcontinue={Uri.EscapeDataString(cont)}" : "");
            using var doc = JsonDocument.Parse(await GetAsync(url));
            var page = FirstPage(doc);
            if (page.TryGetProperty("links", out var arr))
                foreach (var l in arr.EnumerateArray())
                    if (l.TryGetProperty("title", out var lt) && lt.GetString() is { Length: > 0 } s)
                        links.Add(s);
            if (!doc.RootElement.TryGetProperty("continue", out var c) ||
                !c.TryGetProperty("plcontinue", out var pc)) break;
            cont = pc.GetString();
        }
        return links;
    }

    private async Task<string> GetAsync(string url)
    {
        await Task.Delay(PolitenessDelayMs);
        return await Http.GetStringAsync(url);
    }

    private static JsonElement FirstPage(JsonDocument doc)
    {
        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        foreach (var p in pages.EnumerateObject()) return p.Value;
        return default;
    }

    // ============================================================================================
    // Helpers shared by the pipeline (candidate filtering, title normalisation, structural parsing).
    // ============================================================================================

    /// <summary>Two titles refer to the same article (case/underscore-insensitive; redirects were already
    /// resolved at fetch time, so plain normalisation is enough for edge detection).</summary>
    public static bool SameTitle(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    public static string Normalise(string title) => title.Replace('_', ' ').Trim();

    /// <summary>Filter obvious non-topic links out of the curator's candidate list.</summary>
    public static bool IsTopicCandidate(string title) =>
        !Regex.IsMatch(title, @"^(List of|Index of|Outline of|Glossary of|Timeline of|History of the)\b",
            RegexOptions.IgnoreCase)
        && !title.Contains("(disambiguation)", StringComparison.OrdinalIgnoreCase)
        && !title.Contains(':');                      // stray namespaced links

    /// <summary>Cut an extract to a character budget at a sentence boundary (LLM input control).</summary>
    public static string TruncateAtSentence(string text, int budget)
    {
        if (text.Length <= budget) return text;
        var cut = text.LastIndexOfAny(new[] { '.', '!', '?' }, budget - 1);
        return cut > budget / 2 ? text[..(cut + 1)] : text[..budget];
    }

    // ---- The structural fallback: '== Heading ==' sections -> groups (used when the LLM fails). ----

    private static readonly Regex HeaderLine = new(@"^(={2,})\s*(.+?)\s*={2,}\s*$", RegexOptions.Compiled);

    public static List<DistilledGroup> ParseSections(string extract, int maxSections = 5, int maxThorts = 4)
    {
        var groups = new List<DistilledGroup>();
        var heading = "Introduction";
        var body = new StringBuilder();

        void Flush()
        {
            if (groups.Count < maxSections && body.Length > 0 && !IsBoilerplate(heading))
            {
                var thorts = SplitSentences(body.ToString()).Take(maxThorts)
                    .Select(s => new DistilledThort(s, Categories.Default)).ToList();
                if (thorts.Count > 0) groups.Add(new DistilledGroup(heading, thorts));
            }
            body.Clear();
        }

        foreach (var line in extract.Replace("\r\n", "\n").Split('\n'))
        {
            var m = HeaderLine.Match(line.Trim());
            if (m.Success)
            {
                if (m.Groups[1].Value.Length == 2) { Flush(); heading = m.Groups[2].Value.Trim(); }
                continue;                             // level 3+ heading: flatten into its parent
            }
            body.AppendLine(line);
        }
        Flush();
        return groups;
    }

    private static bool IsBoilerplate(string heading) =>
        Regex.IsMatch(heading,
            @"^(See also|References|Notes|Footnotes|Further reading|External links|Bibliography|Citations|Sources|Gallery|Explanatory notes)$",
            RegexOptions.IgnoreCase);

    public static IEnumerable<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        text = Regex.Replace(text, @"\s+", " ").Trim();
        foreach (var raw in Regex.Split(text, @"(?<=[.!?])\s+(?=[A-Z0-9])"))
        {
            var s = Regex.Replace(raw, @"\[\d+\]", "").Trim();   // drop [1]-style citation markers
            if (s.Length < 15) continue;                          // skip fragments
            if (s.Length > 90) s = s[..87].TrimEnd() + "…";       // thorts are short ideas
            yield return s;
        }
    }
}
