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
        if (pending.Count == 0) { Console.WriteLine("  nothing to link."); return; }

        foreach (var group in pending.GroupBy(e => e.A, StringComparer.OrdinalIgnoreCase))
        {
            var from = manifest.Built[group.Key];
            await engine.OpenSphereAsync(from.LocalId);
            foreach (var edge in group)
            {
                var to = manifest.Built[edge.B];
                var (code, _, _) = await engine.LinkSphereAsync(to.LocalId);
                if (code == HttpStatusCode.OK)
                {
                    manifest.LinkedEdges.Add(edge);
                    Console.WriteLine($"  linked {edge.A} <-> {edge.B}");
                }
                else Console.Error.WriteLine($"  LINK FAILED {edge.A} <-> {edge.B}: {code}");
                await Task.Delay(300);                                          // be kind to the cloud
            }
            await engine.SaveAsync();
            manifest.Save(runDir);                                              // resumable after every sphere
        }
    }
}
