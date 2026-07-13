using System.Text;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// LLM call 1 of 3 — choose WHICH pages form the cluster. The model sees the seed's summary and its
/// outbound-link titles and picks the topics that make a well-connected subgraph; the EDGES are then
/// computed from Wikipedia link data by code (an edge exists where either page links the other), so the
/// result is a genuine graph — bring any sphere to the centre and its neighbours are its real
/// conceptual neighbours, not spokes of the seed.
/// </summary>
public static class Curator
{
    private const int MaxCandidates = 350;

    public static async Task<List<PlannedTopic>> ChooseAsync(LlmJson llm, WikiPage seed, int clusterSize)
    {
        var candidates = seed.Links
            .Where(WikipediaClient.IsTopicCandidate)
            .Where(l => !WikipediaClient.SameTitle(l, seed.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException($"\"{seed.Title}\" has no linkable candidate topics.");

        var wanted = Math.Max(1, clusterSize - 1);        // the seed is always in the cluster
        var system =
            "You curate a cluster of encyclopedia topics that will be built as a NETWORK of connected " +
            "Thortspace spheres (one sphere per topic; links between spheres where the articles reference " +
            "each other). Choose topics that form a WELL-CONNECTED subgraph around the seed: prefer topics " +
            "with many mutual relationships to the other chosen topics over prestigious-but-isolated ones, " +
            "and prefer central, substantial subjects over narrow trivia.\n" +
            "Reply with ONLY a JSON object, no prose, in exactly this shape:\n" +
            "{ \"topics\": [ { \"title\": \"<a title copied VERBATIM from the candidate list>\", " +
            "\"why\": \"<one short line: why it belongs / what it connects to>\" } ] }";
        var user = new StringBuilder()
            .AppendLine($"Seed topic: {seed.Title}")
            .AppendLine($"Seed summary: {WikipediaClient.TruncateAtSentence(seed.Extract, 1200)}")
            .AppendLine()
            .AppendLine($"Choose exactly {wanted} topics from these candidates (titles verbatim):")
            .AppendLine(string.Join("; ", candidates))
            .ToString();

        using var doc = await llm.CompleteJsonAsync(system, user)
            ?? throw new InvalidOperationException("The curator LLM call failed twice — cannot plan the cluster.");

        // Code disposes: only verbatim candidates survive, deduped, capped; the seed leads the list.
        var chosen = new List<PlannedTopic> { new(seed.Title, "the seed topic") };
        foreach (var t in LlmJson.Arr(doc.RootElement, "topics"))
        {
            var title = LlmJson.Str(t, "title");
            var match = candidates.FirstOrDefault(c => WikipediaClient.SameTitle(c, title));
            if (match == null) continue;                                       // not from the list — discard
            if (chosen.Any(c => WikipediaClient.SameTitle(c.Title, match))) continue;
            chosen.Add(new PlannedTopic(match, LlmJson.Str(t, "why")));
            if (chosen.Count >= clusterSize) break;
        }
        if (chosen.Count < 3)
            throw new InvalidOperationException(
                $"Curation produced only {chosen.Count} usable topics — not enough for a network.");
        return chosen;
    }

    /// <summary>The cluster graph, from data: an edge wherever either page's outbound links contain the
    /// other topic. Isolated topics are dropped (a sphere with no neighbours defeats the point);
    /// weakly-connected ones are kept but reported.</summary>
    public static (List<Edge> edges, List<string> dropped) ComputeEdges(
        List<PlannedTopic> topics, IReadOnlyDictionary<string, WikiPage> pages)
    {
        var edges = new List<Edge>();
        for (var i = 0; i < topics.Count; i++)
            for (var j = i + 1; j < topics.Count; j++)
            {
                var a = topics[i].Title;
                var b = topics[j].Title;
                if (LinksTo(pages, a, b) || LinksTo(pages, b, a))
                    edges.Add(new Edge(a, b));
            }

        var dropped = new List<string>();
        foreach (var t in topics.ToList())
        {
            var degree = edges.Count(e => e.Touches(t.Title));
            if (degree == 0) { dropped.Add(t.Title); topics.Remove(t); }
            else if (degree < 2) Console.WriteLine($"  note: \"{t.Title}\" has only {degree} edge(s) — thin neighbourhood.");
        }
        edges.RemoveAll(e => dropped.Any(d => e.Touches(d)));
        return (edges, dropped);
    }

    private static bool LinksTo(IReadOnlyDictionary<string, WikiPage> pages, string from, string to) =>
        pages.TryGetValue(from, out var p) && p.Links.Any(l => WikipediaClient.SameTitle(l, to));
}
