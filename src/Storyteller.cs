using System.Text;
using System.Text.Json;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// LLM call 3 of 3 — journeys as STORIES ACROSS THE NETWORK, not per-sphere tours. A Thortspace journey
/// step is a viewpoint (sphere + arrangement + focus + framing + narration); playback FLIES between
/// linked spheres and ANIMATES the regroup when consecutive steps change arrangement. So the model is
/// given the whole built network (topics, both grouping axes, sample thorts, and the EDGE LIST) and asked
/// for a few genuinely different paths through the same territory — with one hard rule, enforced again
/// in code: consecutive steps must be on the same sphere or on directly-linked spheres.
/// </summary>
public static class Storyteller
{
    private const int MinSteps = 6, MaxSteps = 14;

    public static async Task<List<Story>> ComposeAsync(LlmJson llm, Manifest manifest, int journeyCount)
    {
        var digest = BuildDigest(manifest);
        var system =
            "You write guided journeys ('stories') across a NETWORK of Thortspace spheres (one sphere per " +
            "topic; links listed below are the ONLY corridors between spheres). A journey is an ordered " +
            "sequence of viewpoints. Craft each journey as a genuine story with a beginning, development and " +
            "an arrival — not a list of stops. Techniques that play well: open WIDE on the starting sphere; " +
            "switch arrangement mid-sphere to make the thorts visibly REGROUP around a new axis; cross to a " +
            "linked sphere when the story naturally continues there.\n" +
            "HARD RULE: consecutive steps must be on the SAME topic or on topics joined by a link from the " +
            "edge list. Journeys violating this are rejected.\n" +
            "Reply with ONLY a JSON object, no prose, exactly this shape:\n" +
            "{ \"journeys\": [ { \"name\": \"<≤40 chars>\", \"blurb\": \"<one line>\", \"steps\": [ {\n" +
            "    \"topic\": \"<a topic title verbatim>\",\n" +
            "    \"arrangement\": \"primary\" | \"alt\",\n" +
            "    \"focusGroup\": \"<a group name from that topic+arrangement, or null for a whole-sphere step>\",\n" +
            "    \"framing\": \"group\" | \"wide\",\n" +
            "    \"narration\": \"<≤220 chars — what the viewer should notice HERE>\",\n" +
            "    \"title\": \"<≤35 chars step title, no numbering>\",\n" +
            "    \"transition\": \"<ONLY on the first step after changing topic: one line that carries the story across the link>\"\n" +
            "} ] } ] }\n" +
            $"Rules: exactly {journeyCount} journeys; {MinSteps}-{MaxSteps} steps each; each journey takes a " +
            "DIFFERENT route/theme; \"alt\" only on topics listed with an alternative axis; at least one " +
            "arrangement switch somewhere in each journey if the route allows it.";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var doc = await llm.CompleteJsonAsync(system, digest);
            if (doc == null) break;
            var (stories, errors) = Validate(doc.RootElement, manifest);
            if (stories.Count > 0 && errors.Count == 0) return stories;
            if (attempt == 0 && errors.Count > 0)
            {
                Console.WriteLine($"  storyteller: {errors.Count} route error(s) — retrying with the error report.");
                digest += "\n\nYour previous reply had these errors — fix them and reply with the corrected JSON only:\n- " +
                          string.Join("\n- ", errors);
            }
            else if (stories.Count > 0)
            {
                Console.WriteLine($"  storyteller: keeping {stories.Count} valid journey(s), dropping the rest.");
                return stories;
            }
        }
        return new List<Story>();
    }

    /// <summary>What the model gets to work with: every topic's groups on both axes (+ a taste of the
    /// thorts, for narration quality) and the edge list that constrains the routes.</summary>
    private static string BuildDigest(Manifest manifest)
    {
        var sb = new StringBuilder().AppendLine($"The sphere network (seed: {manifest.Seed}):").AppendLine();
        foreach (var (title, d) in manifest.Distillations)
        {
            if (!manifest.Built.ContainsKey(title)) continue;
            sb.AppendLine($"TOPIC: {title} — {d.Summary}");
            sb.AppendLine($"  {d.PrimaryAxis} (primary): " + string.Join(" | ",
                d.Groups.Select(g => $"{g.Name} [{string.Join("; ", g.Thorts.Take(2).Select(t => t.Text))}]")));
            if (d.Alternative != null)
                sb.AppendLine($"  {d.Alternative.Axis} (alt): " + string.Join(" | ", d.Alternative.Groups.Select(g => g.Name)));
        }
        sb.AppendLine();
        sb.AppendLine("LINKS (the only corridors between topics):");
        foreach (var e in manifest.Edges)
            if (manifest.Built.ContainsKey(e.A) && manifest.Built.ContainsKey(e.B))
                sb.AppendLine($"  {e.A} <-> {e.B}");
        return sb.ToString();
    }

    // ---- code disposes: resolve every reference, walk every route, reject illegal hops ----

    private static (List<Story> stories, List<string> errors) Validate(JsonElement root, Manifest manifest)
    {
        var stories = new List<Story>();
        var errors = new List<string>();
        foreach (var j in LlmJson.Arr(root, "journeys"))
        {
            var name = LlmJson.Str(j, "name");
            if (name.Length == 0) { errors.Add("a journey has no name"); continue; }
            var steps = new List<StoryStep>();
            var ok = true;
            string? prevTopic = null;
            foreach (var s in LlmJson.Arr(j, "steps"))
            {
                var topicRaw = LlmJson.Str(s, "topic");
                var topic = manifest.Built.Keys.FirstOrDefault(t => WikipediaClient.SameTitle(t, topicRaw));
                if (topic == null) { errors.Add($"{name}: unknown/unbuilt topic \"{topicRaw}\""); ok = false; break; }

                if (prevTopic != null && !WikipediaClient.SameTitle(prevTopic, topic)
                    && !manifest.Edges.Any(e => e.Is(prevTopic, topic)))
                { errors.Add($"{name}: illegal hop \"{prevTopic}\" -> \"{topic}\" (no link)"); ok = false; break; }

                var built = manifest.Built[topic];
                var arrangement = LlmJson.Str(s, "arrangement", "primary").ToLowerInvariant() == "alt" ? "alt" : "primary";
                if (arrangement == "alt" && built.AltArrangementId == null)
                    arrangement = "primary";                                     // silent downgrade — not an error

                var focusRaw = LlmJson.Str(s, "focusGroup");
                string? focus = null;
                if (focusRaw.Length > 0 && !focusRaw.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    var map = arrangement == "alt" ? built.AltGroups : built.PrimaryGroups;
                    focus = map.Keys.FirstOrDefault(k => k.Equals(focusRaw.Trim(), StringComparison.OrdinalIgnoreCase));
                    // an unresolvable focus degrades to a whole-sphere step rather than killing the story
                }

                var narration = LlmJson.Str(s, "narration");
                if (narration.Length == 0) continue;
                if (narration.Length > 220) narration = narration[..219] + "…";

                steps.Add(new StoryStep(
                    topic, arrangement, focus, null,
                    focus != null ? LlmJson.Str(s, "framing", "group") : "wide",
                    narration,
                    NullIfEmpty(LlmJson.Str(s, "title")),
                    NullIfEmpty(LlmJson.Str(s, "transition"))));
                prevTopic = topic;
            }
            if (!ok) continue;
            if (steps.Count < MinSteps) { errors.Add($"{name}: only {steps.Count} usable steps (need {MinSteps})"); continue; }
            stories.Add(new Story(name, LlmJson.Str(j, "blurb"), steps.Take(MaxSteps).ToList()));
        }
        return (stories, errors);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
