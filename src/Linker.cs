using System.Net;
using Thortspace.Headless;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// The link pass — runs AFTER every sphere exists (link_sphere needs both endpoints). Walks the
/// cluster's edge list and makes each link once (links are bidirectional). Edges are grouped by
/// sphere so each sphere is opened once, linked to all its pending neighbours, then saved.
/// This is what makes the result a GRAPH: any sphere brought to the centre shows its own genuine
/// neighbourhood, not spokes of the seed.
/// </summary>
public static class Linker
{
    public static async Task LinkAsync(HeadlessEngine engine, Manifest manifest, string runDir)
    {
        var pending = manifest.Edges
            .Where(e => manifest.Built.ContainsKey(e.A) && manifest.Built.ContainsKey(e.B) && !manifest.IsLinked(e))
            .ToList();
        if (pending.Count == 0) Console.WriteLine("  nothing to link (already linked).");

        foreach (var group in pending.GroupBy(e => e.A, StringComparer.OrdinalIgnoreCase))
        {
            var from = manifest.Built[group.Key];
            await CloudOp.WithTimeout(engine.OpenSphereAsync(from.LocalId), $"open \"{group.Key}\"");
            var made = new List<Edge>();
            foreach (var edge in group)
            {
                var to = manifest.Built[edge.B];
                var (code, _, _) = await CloudOp.WithTimeout(engine.LinkSphereAsync(to.LocalId), $"link {edge.A} <-> {edge.B}");
                if (code == HttpStatusCode.OK) { made.Add(edge); Console.WriteLine($"  linked {edge.A} <-> {edge.B}"); }
                else Console.Error.WriteLine($"  LINK FAILED {edge.A} <-> {edge.B}: {code}");
                await Task.Delay(300);                                          // be kind to the cloud
            }
            // LinkSphere only mutates local state — the group's links reach the cloud on SaveAsync. Mark the
            // edges linked ONLY after that flush succeeds, so a stall mid-group re-links the whole group on
            // resume rather than recording links that never persisted.
            await CloudOp.WithTimeout(engine.SaveAsync(), $"save \"{group.Key}\"");
            manifest.LinkedEdges.AddRange(made);
            manifest.Save(runDir);
        }

        // ---- link-layout pass: spread every sphere's neighbours around it, in BOTH arrangements ----
        // LinkSphere only positions the link on the sphere that was OPEN at link time, so a sphere that was
        // never the open one shows all its neighbours piled at the default spot. Open each sphere and
        // distribute its links evenly, once per arrangement. Runs on EVERY invocation (idempotent) and skips
        // spheres already recorded, so it still completes when linking spanned several resumes.
        var toArrange = manifest.Built.Where(kv => !manifest.ArrangedTitles.Contains(kv.Key)).ToList();
        if (toArrange.Count > 0) Console.WriteLine("  arranging links…");
        foreach (var (title, b) in toArrange)
        {
            await CloudOp.WithTimeout(engine.OpenSphereAsync(b.LocalId), $"open \"{title}\" (arrange)");
            engine.SwitchArrangement(Guid.Parse(b.PrimaryArrangementId));
            var n = engine.ArrangeLinks();
            if (b.AltArrangementId != null)
            {
                engine.SwitchArrangement(Guid.Parse(b.AltArrangementId));
                engine.ArrangeLinks();
                engine.SwitchArrangement(Guid.Parse(b.PrimaryArrangementId));   // leave on primary
            }
            await CloudOp.WithTimeout(engine.SaveAsync(), $"save \"{title}\" (arrange)");
            manifest.ArrangedTitles.Add(title);
            manifest.Save(runDir);
            if (n > 0) Console.WriteLine($"    {title}: {n} link(s) spread");
        }
    }
}
