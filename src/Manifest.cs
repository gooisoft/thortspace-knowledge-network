using System.Text.Json;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// The run's persistent state — everything each stage produced, saved to <c>manifest.json</c> in the run
/// directory after every unit of work. Re-running any stage skips what's already recorded, so a crash,
/// rate-limit, or Ctrl-C never orphans the run: just run it again. (Fetched pages are cached separately
/// in <c>pages.json</c> to keep the manifest readable.)
/// </summary>
public sealed class Manifest
{
    public string Seed { get; set; } = "";
    public List<PlannedTopic> Topics { get; set; } = new();
    public List<Edge> Edges { get; set; } = new();
    public Dictionary<string, PageDistillation> Distillations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, BuiltSphere> Built { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Edge> LinkedEdges { get; set; } = new();
    public List<BuiltJourney> Journeys { get; set; } = new();

    public bool IsLinked(Edge e) => LinkedEdges.Any(l => l.Is(e.A, e.B));

    // ---- persistence ----

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Manifest LoadOrNew(string runDir, string seed)
    {
        var path = Path.Combine(runDir, "manifest.json");
        if (File.Exists(path))
        {
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json);
            if (m != null && string.Equals(m.Seed, seed, StringComparison.OrdinalIgnoreCase)) return m;
            if (m != null)
                throw new InvalidOperationException(
                    $"Run directory {runDir} already holds a run for seed \"{m.Seed}\" — use a different --dir for \"{seed}\".");
        }
        return new Manifest { Seed = seed };
    }

    public void Save(string runDir)
    {
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "manifest.json"), JsonSerializer.Serialize(this, Json));
    }

    // ---- the page cache (title -> fetched page), kept beside the manifest ----

    public static Dictionary<string, WikiPage> LoadPages(string runDir)
    {
        var path = Path.Combine(runDir, "pages.json");
        if (!File.Exists(path)) return new Dictionary<string, WikiPage>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, WikiPage>(
            JsonSerializer.Deserialize<Dictionary<string, WikiPage>>(File.ReadAllText(path), Json)!,
            StringComparer.OrdinalIgnoreCase);
    }

    public static void SavePages(string runDir, Dictionary<string, WikiPage> pages)
    {
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "pages.json"), JsonSerializer.Serialize(pages, Json));
    }
}
