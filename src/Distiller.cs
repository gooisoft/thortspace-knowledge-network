using System.Text;
using System.Text.Json;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// LLM call 2 of 3 — distill ONE page into sphere content. The aim is a sphere that is BETTER than the
/// article: a handful of named groups holding short, self-contained thorts; typed relationships tracing
/// the argument; and an ALTERNATIVE grouping on a genuinely different axis (chronological, by-school, …)
/// so the sphere has two arrangements — Thortspace animates the regroup when a journey switches between
/// them. The model proposes content and structure ONLY: no ids, no coordinates, no layout (code owns
/// those). If the model fails twice, a structural fallback (sections → groups) keeps the pipeline moving.
/// </summary>
public static class Distiller
{
    private const int ExtractBudget = 7000;       // chars of article text the model sees
    private const int MaxGroups = 6, MinGroups = 3;
    private const int MaxThorts = 24, MaxThortChars = 90;
    private const int MaxPaths = 6;

    public static async Task<PageDistillation> DistillAsync(LlmJson llm, WikiPage page, IEnumerable<string> neighbourTitles)
    {
        var system =
            "You distill an encyclopedia article into a Thortspace sphere: a 3D thinking canvas where short " +
            "'thorts' (one idea each) sit in named groups, with typed relationship paths between groups. " +
            "Be selective — capture what makes the subject MATTER, drop encyclopedic detail. Reply with ONLY " +
            "a JSON object, no prose, exactly this shape:\n" +
            "{\n" +
            "  \"summary\": \"<one sentence: what this subject is>\",\n" +
            "  \"primaryAxis\": \"<2-3 word name for the main grouping axis, e.g. 'Themes'>\",\n" +
            $"  \"groups\": [ {{ \"name\": \"<≤30 chars>\", \"thorts\": [ {{ \"text\": \"<≤{MaxThortChars} chars, a complete self-contained idea>\", " +
            $"\"category\": \"<one of: {string.Join(" | ", Categories.Vocabulary)}>\" }} ] }} ],\n" +
            "  \"paths\": [ { \"from\": \"<a group name>\", \"to\": \"<a group name>\", \"type\": \"<≤20 chars lowercase verb phrase, e.g. 'gives rise to'>\" } ],\n" +
            "  \"alternative\": { \"axis\": \"<2-3 word name for a GENUINELY different grouping axis, e.g. 'Chronological'>\",\n" +
            "                   \"groups\": [ { \"name\": \"<≤30 chars>\", \"thortTexts\": [ \"<texts copied VERBATIM from groups above>\" ] } ] }\n" +
            "}\n" +
            $"Rules: {MinGroups}-{MaxGroups} groups; 12-{MaxThorts} thorts TOTAL; every thort text unique and " +
            "readable on its own; 2-" + MaxPaths + " paths between group NAMES you defined; the alternative " +
            "redistributes THE SAME thort texts (every one of them) into 2 to " +
            "as-many-groups-as-the-primary, on a different conceptual axis.";
        var user = new StringBuilder()
            .AppendLine($"Article: {page.Title}")
            .AppendLine($"Neighbouring topics in this sphere network (context only): {string.Join(", ", neighbourTitles)}")
            .AppendLine()
            .AppendLine(WikipediaClient.TruncateAtSentence(page.Extract, ExtractBudget))
            .ToString();

        using var doc = await llm.CompleteJsonAsync(system, user);
        if (doc == null)
        {
            Console.WriteLine($"  \"{page.Title}\": LLM distillation failed — using the structural fallback.");
            return Fallback(page);
        }
        var distilled = Validate(doc.RootElement, page);
        if (distilled == null)
        {
            Console.WriteLine($"  \"{page.Title}\": distillation failed validation — using the structural fallback.");
            return Fallback(page);
        }
        return distilled;
    }

    // ---- code disposes: bound, dedupe, resolve-by-name, repair or reject ----

    private static PageDistillation? Validate(JsonElement root, WikiPage page)
    {
        var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<DistilledGroup>();
        var total = 0;
        foreach (var g in LlmJson.Arr(root, "groups"))
        {
            var name = Clip(LlmJson.Str(g, "name"), 30);
            if (name.Length == 0 || groups.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var thorts = new List<DistilledThort>();
            foreach (var t in LlmJson.Arr(g, "thorts"))
            {
                var text = Clip(LlmJson.Str(t, "text"), MaxThortChars);
                if (text.Length < 8 || !seenTexts.Add(text) || total >= MaxThorts) continue;
                var cat = Categories.Vocabulary.FirstOrDefault(
                              c => c.Equals(LlmJson.Str(t, "category"), StringComparison.OrdinalIgnoreCase))
                          ?? Categories.Default;
                thorts.Add(new DistilledThort(text, cat));
                total++;
            }
            if (thorts.Count > 0 && groups.Count < MaxGroups) groups.Add(new DistilledGroup(name, thorts));
        }
        if (groups.Count < MinGroups || total < 8) return null;    // too thin to be worth building

        var paths = new List<DistilledPath>();
        foreach (var p in LlmJson.Arr(root, "paths"))
        {
            var from = FindGroup(groups, LlmJson.Str(p, "from"));
            var to = FindGroup(groups, LlmJson.Str(p, "to"));
            var type = Clip(LlmJson.Str(p, "type"), 20).ToLowerInvariant();
            if (from == null || to == null || from == to || type.Length == 0 || paths.Count >= MaxPaths) continue;
            paths.Add(new DistilledPath(from, to, type));
        }

        // The alternative grouping: same texts, different axis. Unknown texts are discarded; texts the
        // model forgot are appended to the last alt group (full coverage keeps arrangement 2 meaningful).
        AltGrouping? alt = null;
        if (root.TryGetProperty("alternative", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            var altGroups = new List<AltGroup>();
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in LlmJson.Arr(a, "groups"))
            {
                var name = Clip(LlmJson.Str(g, "name"), 30);
                if (name.Length == 0 || altGroups.Count >= groups.Count) continue;
                var texts = new List<string>();
                foreach (var t in LlmJson.Arr(g, "thortTexts"))
                {
                    if (t.ValueKind != JsonValueKind.String) continue;
                    var match = seenTexts.FirstOrDefault(s => s.Equals(Clip(t.GetString() ?? "", MaxThortChars),
                        StringComparison.OrdinalIgnoreCase));
                    if (match != null && covered.Add(match)) texts.Add(match);
                }
                if (texts.Count > 0) altGroups.Add(new AltGroup(name, texts));
            }
            if (altGroups.Count >= 2)
            {
                var missed = seenTexts.Where(s => !covered.Contains(s)).ToList();
                if (missed.Count > 0) altGroups[^1].ThortTexts.AddRange(missed);
                alt = new AltGrouping(Clip(LlmJson.Str(a, "axis", "Another view"), 24), altGroups);
            }
        }

        return new PageDistillation(
            page.Title, page.Url,
            Clip(LlmJson.Str(root, "summary"), 200),
            Clip(LlmJson.Str(root, "primaryAxis", "Themes"), 24),
            groups, paths, alt, LlmDistilled: true);
    }

    /// <summary>No-LLM fallback: '== Heading ==' sections become groups, chained with plain paths.
    /// No alternative arrangement (a mechanical second axis would be noise, not insight).</summary>
    private static PageDistillation Fallback(WikiPage page)
    {
        var groups = WikipediaClient.ParseSections(page.Extract);
        var paths = new List<DistilledPath>();
        for (var i = 0; i + 1 < groups.Count; i++)
            paths.Add(new DistilledPath(groups[i].Name, groups[i + 1].Name, i == 0 ? "introduces" : "then"));
        var summary = groups.Count > 0 && groups[0].Thorts.Count > 0 ? groups[0].Thorts[0].Text : page.Title;
        return new PageDistillation(page.Title, page.Url, summary, "Sections", groups, paths, null, LlmDistilled: false);
    }

    private static string? FindGroup(List<DistilledGroup> groups, string name) =>
        groups.FirstOrDefault(g => g.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;

    private static string Clip(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }
}
