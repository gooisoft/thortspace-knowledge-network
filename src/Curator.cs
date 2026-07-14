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

    public const int MaxLinksPerSphere = 5;

    /// <summary>LLM call 1b — PRUNE the near-complete candidate graph down to the most SIGNIFICANT links so the
    /// network reads as a structure, not a morass. The model ranks each sphere's neighbours; we keep an edge if
    /// EITHER endpoint ranked the other in its top-N (≈N links per sphere). Code then guarantees the result is
    /// still a single CONNECTED graph (no orphan sphere, one component), preferring MUTUAL links (both articles
    /// reference each other) when it has to add one back.</summary>
    public static async Task<List<Edge>> SelectSignificantAsync(
        LlmJson llm, List<PlannedTopic> topics, List<Edge> candidates,
        IReadOnlyDictionary<string, WikiPage> pages, int maxPerSphere)
    {
        if (candidates.Count == 0) return candidates;
        string Other(Edge e, string t) => WikipediaClient.SameTitle(e.A, t) ? e.B : e.A;
        List<string> NeighboursOf(string t) => candidates.Where(e => e.Touches(t)).Select(e => Other(e, t))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool Mutual(string a, string b) => LinksTo(pages, a, b) && LinksTo(pages, b, a);

        var system =
            "You rank the connections in a network of encyclopedia-topic spheres. For EACH topic, from its " +
            "candidate neighbours choose AT MOST " + maxPerSphere + " that are the MOST SIGNIFICANT — the " +
            "connections that best convey how the subject relates to the others (defining, foundational or " +
            "strongly explanatory relationships), NOT incidental mentions. Reply with ONLY JSON, no prose:\n" +
            "{ \"links\": [ { \"topic\": \"<verbatim title>\", \"neighbours\": [ \"<verbatim neighbour title>\" ] } ] }";
        var user = new StringBuilder().AppendLine("Topics and their candidate neighbours:");
        foreach (var t in topics)
            user.AppendLine($"- {t.Title}: {string.Join(", ", NeighboursOf(t.Title))}");

        var kept = new HashSet<string>(StringComparer.Ordinal);   // undirected keys "ab" (sorted)
        string Key(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "" + b : b + "" + a;
        Edge Cand(string a, string b) => candidates.FirstOrDefault(e => e.Is(a, b));

        try
        {
            using var doc = await llm.CompleteJsonAsync(system, user.ToString());
            if (doc != null)
                foreach (var l in LlmJson.Arr(doc.RootElement, "links"))
                {
                    var topic = topics.FirstOrDefault(t => WikipediaClient.SameTitle(t.Title, LlmJson.Str(l, "topic")))?.Title;
                    if (topic == null) continue;
                    var picked = 0;
                    foreach (var n in LlmJson.Arr(l, "neighbours"))
                    {
                        if (picked >= maxPerSphere) break;
                        var nb = topics.FirstOrDefault(t => WikipediaClient.SameTitle(t.Title, n.GetString() ?? ""))?.Title;
                        if (nb == null || Cand(topic, nb) == null) continue;   // must be a real candidate edge
                        kept.Add(Key(topic, nb)); picked++;
                    }
                }
        }
        catch { /* fall through to the structural fallback below */ }

        // Build the pruned edge list from what survived.
        var pruned = candidates.Where(e => kept.Contains(Key(e.A, e.B))).ToList();
        if (pruned.Count == 0) pruned = candidates.ToList();   // ranking produced nothing usable → keep all

        // ---- connectivity guards (code disposes) ----
        // (a) no orphan sphere — every topic keeps at least its strongest candidate (mutual first).
        foreach (var t in topics)
        {
            if (pruned.Any(e => e.Touches(t.Title))) continue;
            var best = candidates.Where(e => e.Touches(t.Title))
                .OrderByDescending(e => Mutual(e.A, e.B) ? 1 : 0).FirstOrDefault();
            if (best != null && pruned.All(e => !e.Is(best.A, best.B))) pruned.Add(best);
        }
        // (b) single component — bridge separate components with the strongest candidate edge between them.
        var comp = Components(topics.Select(t => t.Title).ToList(), pruned);
        while (comp.Count > 1)
        {
            var c0 = comp[0];
            Edge bridge = candidates
                .Where(e => (c0.Contains(e.A) && !c0.Contains(e.B)) || (c0.Contains(e.B) && !c0.Contains(e.A)))
                .OrderByDescending(e => Mutual(e.A, e.B) ? 1 : 0).FirstOrDefault();
            if (bridge == null) break;                          // components genuinely unreachable — leave as is
            if (pruned.All(e => !e.Is(bridge.A, bridge.B))) pruned.Add(bridge);
            comp = Components(topics.Select(t => t.Title).ToList(), pruned);
        }
        return pruned;
    }

    // Connected components over the given nodes + edges (title equality via SameTitle).
    private static List<HashSet<string>> Components(List<string> nodes, List<Edge> edges)
    {
        var comps = new List<HashSet<string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in nodes)
        {
            if (!seen.Add(start)) continue;
            var comp = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
            var stack = new Stack<string>(); stack.Push(start);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                foreach (var e in edges.Where(e => e.Touches(cur)))
                {
                    var other = WikipediaClient.SameTitle(e.A, cur) ? e.B : e.A;
                    if (nodes.Any(n => WikipediaClient.SameTitle(n, other)) && comp.Add(other)) { seen.Add(other); stack.Push(other); }
                }
            }
            comps.Add(comp);
        }
        return comps;
    }
}
