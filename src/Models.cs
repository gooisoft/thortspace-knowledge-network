namespace ThortspaceKnowledgeNetwork;

// ============================================================================================
// The data that flows through the pipeline. Everything here is plain and JSON-serialisable —
// the run manifest (Manifest.cs) persists it so every stage is resumable.
// ============================================================================================

/// <summary>A fetched Wikipedia page: canonical title, its URL, plain-text extract, outbound article links.</summary>
public sealed record WikiPage(string Title, string Url, string Extract, List<string> Links);

/// <summary>One topic the curator chose for the cluster (Why = its one-line rationale, kept for the record).</summary>
public sealed record PlannedTopic(string Title, string Why);

/// <summary>An unordered edge of the cluster graph: created when either page links the other.</summary>
public sealed record Edge(string A, string B)
{
    public bool Touches(string title) => Matches(A, title) || Matches(B, title);
    public bool Is(string x, string y) => (Matches(A, x) && Matches(B, y)) || (Matches(A, y) && Matches(B, x));
    private static bool Matches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

// ---- The distiller's output for one page (validated before it reaches the builder). ----

public sealed record PageDistillation(
    string Title,
    string SourceUrl,
    string Summary,                       // one line — used for the record / journey blurbs
    string PrimaryAxis,                   // name of the main arrangement, e.g. "Themes"
    List<DistilledGroup> Groups,          // the primary (thematic) grouping
    List<DistilledPath> Paths,            // group -> group typed relationships
    AltGrouping? Alternative,             // the second axis (same thorts, regrouped) — powers the regroup animation
    bool LlmDistilled);                   // false = the structural fallback produced this

public sealed record DistilledGroup(string Name, List<DistilledThort> Thorts);
public sealed record DistilledThort(string Text, string Category);
public sealed record DistilledPath(string FromGroup, string ToGroup, string Type);
public sealed record AltGrouping(string Axis, List<AltGroup> Groups);
public sealed record AltGroup(string Name, List<string> ThortTexts);

/// <summary>The fixed category vocabulary — we RENAME Thortspace's default pastel palette to these
/// (pastels are deliberate: paths + dark text must read over thort backgrounds).</summary>
public static class Categories
{
    public static readonly string[] Vocabulary = { "Core idea", "Key figure", "Development", "Question", "Detail" };
    public const string Default = "Detail";
}

// ---- The storyteller's output: journeys that walk the network. ----

public sealed record StorySet(List<Story> Journeys);
public sealed record Story(string Name, string Blurb, List<StoryStep> Steps);

/// <summary>One step of a story. Topic = which sphere; Arrangement = "primary" | "alt";
/// FocusGroup/FocusThort name what to frame (by group name / thort text); Transition, when set on the
/// FIRST step on a new sphere, becomes the narration of the auto-inserted bridge step on the PREVIOUS
/// sphere (the neighbourhood view that flies the viewer across the link).</summary>
public sealed record StoryStep(
    string Topic,
    string Arrangement,
    string? FocusGroup,
    string? FocusThort,
    string? Framing,
    string Narration,
    string? Title,
    string? Transition);

// ---- What the builder recorded about each sphere it made (the id plumbing lives here). ----

public sealed class BuiltSphere
{
    public string LocalId { get; set; } = "";
    public long CloudId { get; set; }
    public string PrimaryArrangementId { get; set; } = "";
    public string? AltArrangementId { get; set; }
    public string? AltAxis { get; set; }
    public Dictionary<string, string> PrimaryGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase); // name -> groupId
    public Dictionary<string, string> AltGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);     // name -> groupId
    public Dictionary<string, string> Thorts { get; set; } = new(StringComparer.OrdinalIgnoreCase);        // text -> thortId
}

public sealed record BuiltJourney(string Name, string TripId, int StepCount);
