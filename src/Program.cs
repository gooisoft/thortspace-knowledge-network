using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Thortspace.Headless;

namespace ThortspaceKnowledgeNetwork;

// thortspace-knowledge-network — build a NETWORK of interlinked Thortspace spheres (with cross-network
// journeys) from a Wikipedia topic cluster. Builds on the thortspace-api-starter pattern: reference
// Thortspace.Headless.dll directly and run the engine in-process. See docs/design.md for the full design.
//
//   Run:    dotnet run --project src -- --seed "Philosophy" [--size 12] [--journeys 3]
//                                       [--dir runs/philosophy] [--stages plan,distill,build,link,stories]
//   Needs:  Windows, .NET 8 SDK, an installed Thortspace (the SDK DLLs), an LLM key (e.g. GEMINI_API_KEY),
//           and — for build/link/stories — THORTSPACE_EMAIL/THORTSPACE_PASSWORD (or credentials.json).
//
// The pipeline is RESUMABLE: state lives in <dir>/manifest.json; re-running skips completed work.
internal static class Program
{
    private static readonly string SdkDir = ResolveSdkDir();

    // Mirror the csproj's probing: THORTSPACE_SDK_DIR wins, then the standard x64/ARM64 install locations.
    private static string ResolveSdkDir()
    {
        var env = Environment.GetEnvironmentVariable("THORTSPACE_SDK_DIR");
        if (!string.IsNullOrEmpty(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var install in new[] { "ThortspaceX64", "ThortspaceARM64" })
        {
            var dir = Path.Combine(local, install, "current");
            if (File.Exists(Path.Combine(dir, "Thortspace.Headless.dll"))) return dir;
        }
        return Path.Combine(local, "ThortspaceX64", "current");   // best-effort default for the error message
    }

    private static async Task<int> Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("THORTSPACE_DEBUG") == "1")
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));

        // Resolve Thortspace.Headless.dll + dependencies from the SDK folder at runtime (registered BEFORE
        // any Thortspace type is touched — all engine/LLM work happens inside Run, never inlined here).
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var dll = Path.Combine(SdkDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(dll) ? Assembly.LoadFrom(dll) : null;
        };

        try { return await Run(args); }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine("Could not load the Thortspace SDK: " + ex.Message);
            Console.Error.WriteLine($"Set THORTSPACE_SDK_DIR to the folder containing Thortspace.Headless.dll (looked in: {SdkDir}).");
            return 1;
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> Run(string[] args)
    {
        // ---- configuration ----
        var seed = Arg(args, "--seed") ?? "Philosophy";
        var size = int.TryParse(Arg(args, "--size"), out var n) ? Math.Clamp(n, 3, 40) : 12;
        var journeyCount = int.TryParse(Arg(args, "--journeys"), out var jc) ? Math.Clamp(jc, 1, 5) : 3;
        var runDir = Arg(args, "--dir") ?? Path.Combine("runs", Slug(seed));
        var stages = (Arg(args, "--stages") ?? "plan,distill,build,link,stories")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToHashSet();

        Console.WriteLine($"Seed \"{seed}\" | cluster {size} | journeys {journeyCount} | run dir {runDir}");
        Console.WriteLine($"Stages: {string.Join(" -> ", stages)}");

        var llm = LlmJson.CreateFromEnvironment();
        if (llm == null)
        {
            Console.Error.WriteLine("No LLM key. Set GEMINI_API_KEY (or THORTSPACE_LLM_PROVIDER + THORTSPACE_LLM_KEY) — see README.");
            return 2;
        }
        Console.WriteLine($"LLM: {llm.Describe}");

        var manifest = Manifest.LoadOrNew(runDir, seed);
        var pages = Manifest.LoadPages(runDir);
        var wiki = new WikipediaClient();

        // ============================ plan: curate the cluster + compute the graph ============================
        if (stages.Contains("plan") && manifest.Topics.Count == 0)
        {
            Console.WriteLine("\n== plan ==");
            var seedPage = await FetchCached(wiki, pages, runDir, seed);
            Console.WriteLine($"  seed \"{seedPage.Title}\": {seedPage.Links.Count} outbound links.");
            var topics = await Curator.ChooseAsync(llm, seedPage, size);
            Console.WriteLine($"  curated {topics.Count} topics: {string.Join(", ", topics.Select(t => t.Title))}");

            foreach (var t in topics) await FetchCached(wiki, pages, runDir, t.Title);
            var (edges, dropped) = Curator.ComputeEdges(topics, pages);
            foreach (var d in dropped) Console.WriteLine($"  dropped isolated topic \"{d}\".");
            manifest.Topics = topics;
            manifest.Edges = edges;
            manifest.Save(runDir);
            Console.WriteLine($"  graph: {topics.Count} topics, {edges.Count} edges.");
        }
        if (manifest.Topics.Count == 0) { Console.Error.WriteLine("No plan (run the plan stage first)."); return 3; }

        // ============================ distill: one LLM call per page ============================
        if (stages.Contains("distill"))
        {
            Console.WriteLine("\n== distill ==");
            foreach (var topic in manifest.Topics)
            {
                if (manifest.Distillations.ContainsKey(topic.Title)) continue;
                var page = await FetchCached(wiki, pages, runDir, topic.Title);
                var neighbours = manifest.Edges.Where(e => e.Touches(topic.Title))
                    .Select(e => WikipediaClient.SameTitle(e.A, topic.Title) ? e.B : e.A);
                var d = await Distiller.DistillAsync(llm, page, neighbours);
                manifest.Distillations[topic.Title] = d;
                manifest.Save(runDir);
                Console.WriteLine($"  \"{topic.Title}\": {d.Groups.Count} groups / {d.Groups.Sum(g => g.Thorts.Count)} thorts" +
                                  $" / {d.Paths.Count} paths / alt: {d.Alternative?.Axis ?? "none"}" +
                                  (d.LlmDistilled ? "" : "  [structural fallback]"));
            }
        }

        // The cloud stages need the engine (ONE HeadlessEngine per process; a fixed cache dir per run so
        // resume can reopen spheres by localId — the creator's cache holds the cloud->local map).
        HeadlessEngine? engine = null;
        if (stages.Overlaps(new[] { "build", "link", "stories" }))
        {
            var (email, password) = ResolveCredentials();
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("No Thortspace credentials (needed for build/link/stories) — see README.");
                return 4;
            }
            engine = new HeadlessEngine(Path.Combine(Path.GetTempPath(), "ThortspaceKnowledgeNetwork", Slug(seed)));
            if (await engine.LoginAsync(email, password) != HttpStatusCode.OK)
            { Console.Error.WriteLine("Thortspace login failed."); return 5; }
            Console.WriteLine("\nLogged in to Thortspace.");
        }

        // ============================ build: one PUBLIC sphere per topic ============================
        if (stages.Contains("build"))
        {
            Console.WriteLine("\n== build ==");
            foreach (var topic in manifest.Topics)
            {
                if (manifest.Built.ContainsKey(topic.Title)) continue;
                if (!manifest.Distillations.TryGetValue(topic.Title, out var d))
                { Console.Error.WriteLine($"  \"{topic.Title}\" not distilled — skipping."); continue; }
                Console.WriteLine($"  building \"{topic.Title}\" …");
                manifest.Built[topic.Title] = await Builder.BuildAsync(engine!, d);
                manifest.Save(runDir);
                Console.WriteLine($"    cloudId {manifest.Built[topic.Title].CloudId}");
                await Task.Delay(1500);            // throttle: each public sphere triggers SEO/og-image renders
            }
        }

        // ============================ link: the graph edges (both endpoints must exist) ============================
        if (stages.Contains("link"))
        {
            Console.WriteLine("\n== link ==");
            await Linker.LinkAsync(engine!, manifest, runDir);
        }

        // ============================ stories: cross-network journeys ============================
        if (stages.Contains("stories") && manifest.Journeys.Count == 0)
        {
            Console.WriteLine("\n== stories ==");
            var stories = await Storyteller.ComposeAsync(llm, manifest, journeyCount);
            if (stories.Count == 0) Console.Error.WriteLine("  the storyteller produced no valid journeys.");
            foreach (var story in stories)
            {
                Console.WriteLine($"  authoring \"{story.Name}\" ({story.Steps.Count} steps across " +
                                  $"{story.Steps.Select(s => s.Topic).Distinct().Count()} spheres)…");
                manifest.Journeys.Add(await JourneyAuthor.AuthorAsync(engine!, manifest, story));
                manifest.Save(runDir);
            }
        }

        // ============================ summary ============================
        Console.WriteLine("\n== done ==");
        foreach (var (title, b) in manifest.Built)
            Console.WriteLine($"  {title,-40} https://thort.space/{b.CloudId}");
        Console.WriteLine($"  {manifest.LinkedEdges.Count} links, {manifest.Journeys.Count} journeys" +
                          (manifest.Journeys.Count > 0 ? ": " + string.Join(" | ", manifest.Journeys.Select(j => j.Name)) : "."));
        Console.WriteLine("\nOpen any sphere in Thortspace and bring a neighbour to the centre — the network is a");
        Console.WriteLine("graph, not a hierarchy. Play a journey in Present mode to fly across it. NOTE: journeys");
        Console.WriteLine("cloud-sync only on a sync-enabled account (Premium/Subscriber/internal) — on a free");
        Console.WriteLine("account the spheres publish but the journeys stay local to this machine's cache.");
        return 0;
    }

    // ---- helpers ----

    private static async Task<WikiPage> FetchCached(WikipediaClient wiki, Dictionary<string, WikiPage> pages,
        string runDir, string topic)
    {
        if (pages.TryGetValue(topic, out var cached)) return cached;
        var page = await wiki.FetchAsync(topic);
        pages[topic] = page;
        if (!WikipediaClient.SameTitle(topic, page.Title)) pages[page.Title] = page;   // canonical title too
        Manifest.SavePages(runDir, pages);
        return page;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    // Credentials: (1) THORTSPACE_EMAIL / THORTSPACE_PASSWORD env vars, or (2) a credentials.json —
    // {"email":"...","password":"..."} — at THORTSPACE_CREDENTIALS, else beside the project (gitignored),
    // else the %LOCALAPPDATA%\ThortspaceMcp one shared with the other headless stacks. Plaintext on disk →
    // use a dedicated account; contents are never printed.
    private static (string? email, string? password) ResolveCredentials()
    {
        var email = Environment.GetEnvironmentVariable("THORTSPACE_EMAIL");
        var password = Environment.GetEnvironmentVariable("THORTSPACE_PASSWORD");
        if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password)) return (email, password);

        foreach (var path in new[]
                 {
                     Environment.GetEnvironmentVariable("THORTSPACE_CREDENTIALS"),
                     Path.Combine(Directory.GetCurrentDirectory(), "credentials.json"),
                     Path.Combine(AppContext.BaseDirectory, "credentials.json"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThortspaceMcp", "credentials.json"),
                 })
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (string.IsNullOrEmpty(email) && root.TryGetProperty("email", out var em)) email = em.GetString();
                if (string.IsNullOrEmpty(password) && root.TryGetProperty("password", out var pw)) password = pw.GetString();
                if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password))
                {
                    Console.WriteLine($"Using credentials file: {path}");
                    return (email, password);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"Could not read {path}: {ex.Message}"); }
        }
        return (email, password);
    }
}
