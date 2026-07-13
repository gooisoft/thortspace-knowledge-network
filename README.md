# thortspace-knowledge-network

Build a **network of interlinked [Thortspace](https://thortspace.com) spheres** — with guided journeys
that fly across it — from a Wikipedia topic cluster, programmatically.

Pick a seed topic ("Philosophy", "Photosynthesis", "The Beatles"…) and this pipeline:

1. **curates** a cluster of related articles (an LLM chooses; Wikipedia's own link data defines the graph),
2. **distills** each article into a sphere that aims to be *better than the page* — short thorts in named
   groups, colour categories, typed relationship paths, and **two arrangements** (the same thorts grouped
   on two different axes — Thortspace animates the regroup when you switch),
3. **builds** each sphere through the public headless API,
4. **links** the spheres wherever the underlying articles reference each other — a genuine **graph, not a
   hierarchy**: bring any sphere to the centre and its real conceptual neighbours surround it,
5. **writes journeys** — a few *stories across the whole network*, each a validated route along the
   links: playback flies from sphere to sphere and regroups thorts mid-sphere as the story turns.

It builds on [thortspace-api-starter](https://github.com/gooisoft/thortspace-api-starter) (start there if
this is your first contact with the API): the same in-process model — the Thortspace headless engine runs
inside your process, consumed as the `Thortspace.Headless` NuGet package. No server, no socket.

## The architecture in one line

**LLM proposes, code disposes.** The model is called as a stateless function (three call shapes: curator,
distiller, storyteller) and returns strict JSON — *content and structure only, never ids, coordinates or
layout*. Deterministic C# validates every reply (bounds, name resolution, route legality against the edge
list), repairs or rejects it, and drives the engine. If the model fails, a structural fallback keeps the
pipeline moving.

## Requirements

- Windows + [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer)
- The [`Thortspace.Headless` NuGet package](https://www.nuget.org/packages/Thortspace.Headless) — restores
  automatically from nuget.org; no installed Thortspace app needed
- An LLM — a cloud API key (Gemini / Claude / OpenAI), **or** a local CLI agent (grok / claude / gemini,
  no key), **or** a local model server (Ollama / LM Studio). See [Choosing the model](#choosing-the-model--three-ways-no-lock-in).
- A Thortspace account for the build stages. Note two account realities:
  - free accounts have a **sphere cap** — a 12-topic cluster needs room;
  - **journeys cloud-sync only on a sync-enabled account** (the same gate as private spheres). The
    spheres themselves are PUBLIC and save on any tier.

## Quickstart

```powershell
# credentials for the account the spheres are created in (or use a credentials.json — see below)
$env:THORTSPACE_EMAIL    = "you@example.com"
$env:THORTSPACE_PASSWORD = "..."

# your LLM key (Gemini shown; see "Choosing the model")
$env:GEMINI_API_KEY      = "..."

dotnet run --project src -- --seed "Philosophy" --size 12 --journeys 3
```

The run prints a `https://thort.space/<id>` link per sphere when it finishes.

### Options

| Flag | Default | Meaning |
|---|---|---|
| `--seed` | `Philosophy` | The Wikipedia article the cluster grows from. |
| `--size` | `12` | Cluster size (spheres), including the seed. Start small. |
| `--journeys` | `3` | How many cross-network journeys to write. |
| `--dir` | `runs/<seed>` | Run directory (manifest + page cache). |
| `--stages` | all | Any of `plan,distill,build,link,stories` — run a subset. |

Every stage is **resumable**: state lives in `runs/<seed>/manifest.json`, and re-running skips whatever is
already done. `plan` and `distill` need only the LLM (see below) — you can inspect the distillations in the
manifest before anything touches your account.

### Choosing the model — three ways, no lock-in

The LLM is called as a stateless function; you pick where it runs with `THORTSPACE_LLM_PROVIDER`. A
12-sphere cluster is ~14 calls at a few thousand tokens each.

**1. A cloud API (best quality).** The provider-agnostic client from `Thortspace.Headless` — bring a key:

```powershell
$env:THORTSPACE_LLM_PROVIDER = "google"     # google | anthropic | openai | xai
$env:THORTSPACE_LLM_KEY      = "..."        # or GEMINI_API_KEY / ANTHROPIC_API_KEY / OPENAI_API_KEY
$env:THORTSPACE_LLM_MODEL    = "gemini-flash-latest"   # optional
```

Pennies on a flash-class model.

**2. A local CLI agent (no API key).** Drive a logged-in agent CLI (grok, claude, gemini) as a
subprocess — it rides that tool's own account/subscription, so there's no key to manage:

```powershell
$env:THORTSPACE_LLM_PROVIDER = "grok"       # grok | claude | gemini  (uses the tool's print mode)
# or point at any single-turn print command yourself:
$env:THORTSPACE_LLM_PROVIDER = "cli"
$env:THORTSPACE_LLM_CMD      = "grok -p"    # prompt is appended as the final argument
```

Slower per call (each call boots the agent) but frontier quality without a key. Batch use is subject to
that tool's usage terms.

**3. A local model server (free, offline).** Any OpenAI-compatible endpoint — Ollama, LM Studio, …:

```powershell
$env:THORTSPACE_LLM_PROVIDER = "ollama"     # uses OLLAMA_API_BASE or http://localhost:11434
$env:THORTSPACE_LLM_MODEL    = "llama3.1"   # required — a model you've pulled
# generic: THORTSPACE_LLM_PROVIDER=openai-compatible + THORTSPACE_LLM_BASEURL=http://host:port/v1
```

Note: the engine's HTTP client times out at 45s, so a large local model on modest hardware may not keep
up — prefer a small/fast local model here, or use the CLI-agent path (option 2) which has no such limit.

### Credentials file (instead of env vars)

`credentials.json` (gitignored — never commit it) beside the project, or at `THORTSPACE_CREDENTIALS`:

```json
{ "email": "you@example.com", "password": "..." }
```

Use a dedicated account; the file is plaintext on disk.

### Where the engine comes from

The `Thortspace.Headless` NuGet package — the engine DLL plus its dependency closure, restored and copied
beside your exe like any other package (no installed app needed at build or run time). Set
`THORTSPACE_DEBUG=1` to route the engine's diagnostic trace to stderr.

## What to look at when it's done

- Open any sphere and use the **neighbourhood view**: the linked spheres around it are its real
  conceptual neighbours (edges exist only where the articles reference each other).
- **Switch arrangement** on a sphere: the same thorts regroup around a different axis, animated.
- **Present mode → play a journey**: the story flies across the links, bridging sphere to sphere.

## Content licence

Sphere content is distilled from Wikipedia articles
([CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/)). Every generated sphere carries a
"Source" group whose thort links the article — keep it.

## A note on intent

Thortspace is a *thought-processing* tool — its heart is turning confusion into insight, not storing
knowledge. A generated encyclopedia cluster is a **showcase**: what sphere networks, arrangements and
journeys can do, built at arm's length through the public API. The interesting move is what it suggests:
the same pipeline shape works for *your* material — research notes, a codebase, a decision — anywhere a
graph of connected canvases beats a pile of pages.

## Code licence

MIT — see [LICENSE](LICENSE).
