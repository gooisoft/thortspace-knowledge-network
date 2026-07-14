# Showcase rework — Andrew's 6-item feedback (2026-07-14)

Decisions: link significance = **LLM-ranked**; rollout = **small pilot (~6 spheres) first**, then redo the 15.

## Items → actions

**SDK (`Thortspace.Headless` / `Thortspace.Concepts`) — rides the next release:**
1. **(2) Link positioning** — replace the Fibonacci-spiral in `SphereSession.ArrangeLinks`/`FibonacciSphereDir`
   with the app's even-spread positions (`Thortspace.Concepts.SphereDistributor`, the pre-calc `_staticPointsList`
   xyz table) so linked spheres are maximally/evenly spread, not a snaking spiral. [confirm accessor]
2. **(1) Zoom** — add `Zoom`/`viewMode` to the `GetTrip` projection so the actual value is verifiable; confirm my
   overview/bridge reductions applied; pull in further if still "full moon". Current formula (SphereSession
   AddTripStep): overview `max(cz, r*0.35)`, neighbourhood `r*1.8` (camDist = radius + zoom).
3. **(4-check) Arrangements** — confirm whether the *set* of sphere↔sphere links is per-arrangement or global
   (LinkLocation is per-arrangement `_location[arr]`). If per-arrangement links are supported, use for journeys.

**Generator (`thortspace-knowledge-network`):**
4. **(4) Fewer links** — LLM ranks each sphere's neighbours by significance; keep ~5 per sphere (was near-complete
   mesh, 97/105 for 15). Curator/Linker change.
5. **(5) Journeys cross only real links** — Storyteller routes validated so every bridge is a direct link in the
   reduced set (JourneyAuthor bridges only linked pairs).
6. **(3) Quadrant colours** — force Sphere1/Sphere2 far enough apart (hue+value separation) that quadrants always
   show. `Builder.ThemeForTitle`.
7. **(6) #keywords** — Distiller prompts the LLM to prefix the 1–3 most important words in each thort with `#`
   (colour comes from the thort's category; # words are retained during zoom-out). `Distiller` prompt.

## Rollout
Implement all → regenerate ~6-sphere pilot → Andrew eyeballs zoom / link-spread / colours / link-count /
journey transitions / #keywords → then redo 15 + delete old 15 spheres + duplicate-named journeys.

## Status / notes
- Trip-sync race + first pass at zoom already committed to thortspace (`d0d35ebc2`, local/unpushed).
- The 3 current journeys were regenerated 2026-07-14 (cloud-verified full steps) but Andrew reports zoom STILL
  too far + possible deleted-wrong-duplicate confusion — the clean redo resolves both.
- SDK is used LOCALLY via unobfuscated DLL swap into the generator bin (see memory
  `knowledge-network-generator`); public nuget still 1.6.727 until the fixes ride a release.
