using System.Net;
using System.Text.Json;
using Thortspace.Headless;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// Turns one validated <see cref="PageDistillation"/> into a real PUBLIC sphere via the headless engine:
/// groups + thorts on the hex lattice, an attribution thort (CC BY-SA), the category palette renamed and
/// applied, typed group-to-group paths with strong colours, a topology-aware layout, and — when the
/// distillation carries an alternative axis — a SECOND arrangement that regroups the same thorts
/// (CreateArrangement forks the current arrangement; group membership is per-arrangement, so MoveThort
/// redistributes; the app animates the regroup when a journey switches arrangement).
/// All ids created here are recorded on the returned <see cref="BuiltSphere"/> — the link and journey
/// stages are pure id plumbing over that record.
/// </summary>
public static class Builder
{
    // Strong colours for the first few path types (thort backgrounds stay pastel by design — full
    // saturation belongs on PATHS; same rationale as the api-starter).
    private static readonly (int r, int g, int b)[] PathColours = { (45, 107, 255), (220, 68, 90), (36, 160, 90) };

    public static async Task<BuiltSphere> BuildAsync(HeadlessEngine engine, PageDistillation d)
    {
        var (code, localId, cloudId) = await engine.CreateSphereAsync(d.Title, isPublic: true);
        if (code != HttpStatusCode.OK)
            throw new InvalidOperationException($"Create sphere failed for \"{d.Title}\": {code}");
        await engine.OpenSphereAsync(localId);                                  // create does NOT open

        var built = new BuiltSphere { LocalId = localId, CloudId = cloudId };
        var snap = Snap(engine);
        built.PrimaryArrangementId = snap.RootElement.GetProperty("arrangementId").GetString()!;
        engine.RenameArrangement(Guid.Parse(built.PrimaryArrangementId), d.PrimaryAxis);

        // Give each sphere its OWN colour (deterministic from its title) so the network isn't all grey.
        var (bd, s1, s2) = ThemeForTitle(d.Title);
        engine.SetSphereTheme(bd.r, bd.g, bd.b, s1.r, s1.g, s1.b, s2.r, s2.g, s2.b);

        // A freshly-created sphere ships with a default group of ~7 blank thorts (a starting-point for the
        // user). Left as-is it takes a layout slot (displacing a real group) and — having an empty NAME —
        // would be swept into the alternative-arrangement fork below. We KEEP it (users can fill it in) but
        // (a) exclude it from the alt fork and (b) tuck it at the bottom pole in BOTH arrangements, out of
        // the way of the generated content.
        var defaultGroupIds = SnapGroups(engine).Select(g => g.id).ToList();

        // ---- groups + thorts (first thort makes the group; the rest join it on the hex lattice) ----
        foreach (var g in d.Groups)
        {
            var first = engine.AddThort(g.Thorts[0].Text);
            var groupId = engine.GroupOfThort(first)!.Value;
            built.Thorts[g.Thorts[0].Text] = first.ToString();
            foreach (var t in g.Thorts.Skip(1))
                built.Thorts[t.Text] = engine.AddThort(t.Text, groupId: groupId).ToString();
            engine.RenameGroup(groupId, g.Name);
            engine.ArrangeGroup(groupId, "hex");
            built.PrimaryGroups[g.Name] = groupId.ToString();
        }

        // ---- attribution (CC BY-SA): its own small "Source" group, the thort linking the article ----
        var attribution = engine.AddThort($"Source: Wikipedia — \"{d.Title}\" (CC BY-SA 4.0)", link: d.SourceUrl);
        var sourceGroup = engine.GroupOfThort(attribution)!.Value;
        engine.RenameGroup(sourceGroup, "Source");

        // ---- categories: RENAME the default pastel palette to our vocabulary, then apply per thort ----
        var catIds = CategoryIds(snap);
        for (var i = 0; i < Categories.Vocabulary.Length && i < catIds.Count; i++)
            engine.RenameCategory(Guid.Parse(catIds[i]), Categories.Vocabulary[i]);
        foreach (var g in d.Groups)
            foreach (var t in g.Thorts)
                engine.SetThortCategory(Guid.Parse(built.Thorts[t.Text]), t.Category);

        // ---- typed relationships between groups, with strong colours on the first few types ----
        var colouredTypes = new List<string>();
        foreach (var p in d.Paths)
        {
            engine.Connect(Guid.Parse(built.PrimaryGroups[p.FromGroup]),
                           Guid.Parse(built.PrimaryGroups[p.ToGroup]), p.Type);
            if (!colouredTypes.Contains(p.Type) && colouredTypes.Count < PathColours.Length)
            {
                var c = PathColours[colouredTypes.Count];
                engine.RecolourPathType(p.Type, c.r, c.g, c.b);
                colouredTypes.Add(p.Type);
            }
        }

        // ---- layout: cluster related groups, untangle the paths ----
        engine.Arrange(null, null, null, reduceCrossings: true);
        TuckAway(engine, defaultGroupIds);                                      // blank default group → bottom pole

        // ---- the ALTERNATIVE arrangement: fork, rename the forked groups to the new axis, regroup ----
        if (d.Alternative is { } alt)
        {
            var altId = engine.CreateArrangement(alt.Axis);                     // forks groups + membership
            engine.SwitchArrangement(altId);
            built.AltArrangementId = altId.ToString();
            built.AltAxis = alt.Axis;

            // The fork carries the primary groups (same names, NEW per-arrangement group objects). Reuse
            // the forked CONTENT groups in order as the alt groups; "Source" AND the blank default group keep
            // their identity in both arrangements and must NOT be consumed as alt content groups.
            var forked = SnapGroups(engine)
                .Where(g => g.name != "Source" && !defaultGroupIds.Contains(g.id)).ToList();
            for (var i = 0; i < alt.Groups.Count && i < forked.Count; i++)
            {
                var target = forked[i];
                engine.RenameGroup(Guid.Parse(target.id), alt.Groups[i].Name);
                built.AltGroups[alt.Groups[i].Name] = target.id;
                foreach (var text in alt.Groups[i].ThortTexts)
                    if (built.Thorts.TryGetValue(text, out var thortId))
                        engine.MoveThort(Guid.Parse(thortId), Guid.Parse(target.id), null, null);
            }
            foreach (var (name, id) in built.AltGroups) engine.ArrangeGroup(Guid.Parse(id), "hex");
            engine.Arrange(null, null, null, reduceCrossings: true);
            TuckAway(engine, defaultGroupIds);                                  // also tuck it in the alt arrangement
            engine.SwitchArrangement(Guid.Parse(built.PrimaryArrangementId));   // leave the sphere on primary
        }

        var save = await engine.SaveAsync();
        if (save != HttpStatusCode.OK)
            throw new InvalidOperationException($"Save failed for \"{d.Title}\": {save} " +
                "(public spheres save on any tier — check the account/login).");
        return built;
    }

    // ---- snapshot readers (Snapshot() returns an anonymous object; serialise + pick what we need) ----

    private static JsonDocument Snap(IAgentEngine engine) =>
        JsonDocument.Parse(JsonSerializer.Serialize(engine.Snapshot()));

    // A distinct colour theme per sphere, deterministic from its title: a deep backdrop + a two-tone
    // sphere surface, all on one hue. Dark-ish so the pastel category colours and dark text still read.
    private static ((int r, int g, int b) backdrop, (int r, int g, int b) s1, (int r, int g, int b) s2)
        ThemeForTitle(string title)
    {
        int hash = 0;
        foreach (var ch in title) hash = unchecked(hash * 31 + ch);
        double hue = (((hash % 360) + 360) % 360);
        return (Hsv(hue, 0.55, 0.22), Hsv(hue, 0.60, 0.46), Hsv(hue, 0.50, 0.72));
    }

    private static (int r, int g, int b) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs((h / 60.0) % 2 - 1)), m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    // Move the given groups low-and-to-the-back of the CURRENT arrangement, out of the content's way.
    // NOT the exact bottom pole (0,-1,0): that is anti-parallel to the up-vector (0,1,0) and the group's
    // up-vector solve then yields NaN (BadThoughtsException). A low off-axis direction avoids the singularity.
    private static void TuckAway(IAgentEngine engine, IEnumerable<string> groupIds)
    {
        foreach (var id in groupIds) engine.MoveGroup(Guid.Parse(id), 0f, -0.82f, 0.57f);
    }

    private static List<(string id, string name)> SnapGroups(IAgentEngine engine)
    {
        using var doc = Snap(engine);
        var list = new List<(string, string)>();
        if (doc.RootElement.TryGetProperty("groups", out var groups))
            foreach (var g in groups.EnumerateArray())
                list.Add((g.GetProperty("id").GetString()!, g.GetProperty("name").GetString() ?? ""));
        return list;
    }

    private static List<string> CategoryIds(JsonDocument snap)
    {
        var list = new List<string>();
        if (snap.RootElement.TryGetProperty("categorySet", out var cs) && cs.ValueKind == JsonValueKind.Object
            && cs.TryGetProperty("categories", out var categories))
            foreach (var c in categories.EnumerateArray())
                if (c.TryGetProperty("id", out var id)) list.Add(id.GetString()!);
        return list;
    }
}
