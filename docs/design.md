# Design — thortspace-knowledge-network

**Agreed with Andrew, 2026-07-13.** A showcase generator that turns a Wikipedia topic cluster into a
**network of interlinked Thortspace spheres** with **cross-network journeys**, built on the public
headless API (the same `Thortspace.Headless.dll` the
[thortspace-api-starter](https://github.com/gooisoft/thortspace-api-starter) demonstrates).

## Why (the two products in one move)

1. **The content** — a browsable, interlinked sphere network on a real topic: public spheres, each with
   a crawlable page on thort.space, an og:image, colour categories, two arrangements, and journeys that
   fly across the network. It SHOWCASES what Thortspace can do (Thortspace itself aims at
   *transformational insight*, not knowledge — this is a capability demo, deliberately).
2. **The generator repo** — a worked, non-trivial public example of building sphere networks
   programmatically: the inspiration answer to "what is the API for?".

## Core design decisions

- **A graph, not a hierarchy.** Edges are created wherever BOTH endpoints are in the cluster and either
  Wikipedia page links the other. Bring any sphere to the centre and its neighbourhood is its real
  conceptual neighbours — nothing is routed through the seed sphere.
- **Journeys are cross-network stories, not per-sphere tours** (Andrew, 2026-07-13). A journey step
  carries sphere + arrangement + focus + framing; playback FLIES between linked spheres and ANIMATES
  regrouping when consecutive steps change arrangement. So: 2-3 journeys per cluster, each a story that
  walks edges of the graph, with same-sphere arrangement-switch beats and `networkSphereId` bridge
  steps at sphere boundaries.
- **LLM proposes, code disposes** (house pattern). The LLM is a *stateless distiller* — three call
  types, each returning strict JSON that deterministic C# validates and executes. It never emits
  coordinates (layout belongs to `Arrange`/`ArrangeGroup`), never ids (code owns the id plumbing).
- **BYO LLM key** via `LlmClientFactory` (public in Headless.dll): `GEMINI_API_KEY` /
  `THORTSPACE_LLM_*` env vars. Never the app's `/ai/complete` proxy (that's the app's per-user metered
  channel).
- **Two arrangements per sphere** — the distiller proposes a primary (thematic) grouping AND an
  alternative axis (chronological / by-school / …). `CreateArrangement` forks the current arrangement;
  group membership is per-arrangement (`MoveThort(thortId, targetGroupId)`), so the alt arrangement
  renames the forked groups and redistributes the same thorts → the storyteller gets the animated
  regroup as a story beat.
- **Wikipedia over Grokipedia**: proper API (extracts + links), clean CC BY-SA 4.0. Every sphere
  carries a "Source" group with an attribution thort linking the article.
- **Start small** (Andrew): default cluster size 12. Small-and-excellent over large-and-mediocre —
  protects Explore from pollution and avoids thin-content SEO risk. Throttled sphere creation
  (render/SEO pipeline kindness).

## The three LLM calls

1. **Curator** (once): seed summary + outbound-link candidate titles → the N topics that form a
   well-connected subgraph (prompt biases towards mutual linkage over prestige). Code then fetches each
   chosen topic's links and computes the edge list *from data*.
2. **Distiller** (per page): title + extract → `{primaryAxis, summary, groups[name, thorts[text,
   category]], paths[fromGroup, toGroup, type], alternative{axis, groups[name, thortTexts]}}`.
   Fixed category vocabulary (renames the default pastel palette). Bounds enforced in code; one retry
   on malformed JSON; **structural fallback** (sections → groups, chain paths) if the LLM fails.
3. **Storyteller** (once, after build+link): manifest digest (topics, groups both axes, sample thorts,
   edge list) → 2-3 journey scripts. Hard constraint stated in the prompt AND enforced in code:
   consecutive steps must be same-sphere or a graph edge. One retry with the error report; invalid
   journeys dropped.

## Pipeline stages (resumable via `runs/<slug>/manifest.json`)

`plan` → `distill` → `build` → `link` → `stories`

- **plan** — fetch seed, curate, fetch links per topic, compute edges, warn degree<2 / drop degree 0.
- **distill** — LLM-distill every topic (cached in the run dir; re-runs skip).
- **build** — per topic: create PUBLIC sphere → groups/thorts (hex) → attribution thort (`link:` param)
  → categories (rename palette, assign) → group-to-group paths (+ strong path-type colours) →
  `Arrange(reduceCrossings)` → alt arrangement (fork, rename, `MoveThort`, arrange, switch back) →
  save. Manifest records localId/cloudId/arrangement ids/group+thort id maps.
- **link** — per edge (grouped by sphere to minimise opens): `LinkSphereAsync` (bidirectional), save.
- **stories** — storyteller call → validate routes → author each journey: `CreateTrip`, per-run
  `OpenSphere` + `AddTripStep` (arrangement + focusGroup resolved from the manifest), auto-inserted
  bridge steps (`framing: neighbourhood`, `networkSphereId: next`), `SetTripPublic`.

plan/distill need only the LLM key (no Thortspace account) — contributors can inspect distillations
without building anything.

## Constraints / traps encoded

- **Account**: needs an uncapped (internal/Gooisoft) or roomy account — free tier is capped (new
  registrants: 10 spheres). Spheres are PUBLIC (saves on any tier) but **journeys only cloud-sync on a
  sync-enabled account** (same gate as private spheres) — on a free account the journeys stay local.
- Engine traps (from the starter/memory): ONE `HeadlessEngine` per process; `AssemblyResolve` from
  `THORTSPACE_SDK_DIR` before any Thortspace type is touched; create does NOT open; the creator's cache
  dir holds the cloud→local map (fixed cache dir per run, so resume can reopen by localId).
- Wikipedia politeness: proper UA, ~150 ms between HTTP calls, link pagination capped.
- Category-corruption bug: FIXED since 1.6.718 (`679c09b78`) — categories are safe to apply.

## Verification (Andrew, GUI — generator can't see rendering)

1. Open the seed sphere on thort.space / thort.us / native: groups named + hex-tidy, categories
   coloured, paths typed, attribution thort present and linked.
2. Switch arrangement in-app: the regroup animation plays; alt groups make sense.
3. Neighbourhood view: linked spheres surround the current one; edges match conceptual neighbours
   (graph, not hub-and-spoke).
4. Present mode: play a journey — it flies between spheres at bridges, switches arrangements
   mid-sphere, narration reads well.
5. Judge: is each sphere BETTER than the article? (The quality bar that makes or breaks the showcase.)

## Deferred / open

- Empty leftover forked groups in the alt arrangement when altCount < primaryCount — verify visually;
  if they linger as floating labels, add a cleanup (delete-empty-group op doesn't exist headlessly yet).
- Viewpoint direction + per-step category set aren't exposed on `AddTripStep` (captured implicitly) —
  small API addition if a story ever needs them.
- Cross-sphere journey steps could also use `networkArrangementId` for the neighbour — not used in v1.
- Grokipedia content source — stub only (403-blocks plain HTTP; murkier licence).
- Scale-up beyond one cluster: only after GSC/engagement reads on the pilot (SEO thin-content risk).
- GitHub publication: Andrew's call (repo builds + commits locally first).
