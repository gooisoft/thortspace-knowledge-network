using Thortspace.Headless;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// Authors one validated <see cref="Story"/> as a real Thortspace journey (a "trip"). Trips belong to
/// the ACCOUNT, not to a sphere — each step references whichever sphere is open when it is added — so a
/// cross-network story is authored by opening each sphere along the route in turn and adding its steps
/// to the same trip. At every sphere boundary a BRIDGE step is inserted on the sphere being left:
/// neighbourhood framing + networkSphereId shows the current sphere and the next one side by side, and
/// playback then flies across the link. The storyteller's optional per-step "transition" line becomes
/// the bridge narration.
/// </summary>
public static class JourneyAuthor
{
    public static async Task<BuiltJourney> AuthorAsync(HeadlessEngine engine, Manifest manifest, Story story)
    {
        // The trip is created while the FIRST sphere of the route is open.
        var first = manifest.Built[story.Steps[0].Topic];
        await engine.OpenSphereAsync(first.LocalId);
        var tripId = engine.CreateTrip(story.Name);
        var stepCount = 0;
        var openTopic = story.Steps[0].Topic;

        for (var i = 0; i < story.Steps.Count; i++)
        {
            var step = story.Steps[i];
            if (!WikipediaClient.SameTitle(openTopic, step.Topic))
            {
                // Bridge on the sphere we are leaving, THEN move to the next sphere of the story.
                var next = manifest.Built[step.Topic];
                if (engine.AddTripStep(tripId,
                    step.Transition ?? $"The story continues on \"{step.Topic}\" — one of this sphere's linked neighbours.",
                    arrangementId: null, focusGroupId: null, focusThortId: null,
                    name: $"To {step.Topic}", framing: "neighbourhood", networkSphereId: next.LocalId))
                    stepCount++;
                await engine.OpenSphereAsync(next.LocalId);
                openTopic = step.Topic;
            }

            var built = manifest.Built[step.Topic];
            var arrangementId = step.Arrangement == "alt" && built.AltArrangementId != null
                ? built.AltArrangementId : built.PrimaryArrangementId;
            var focusGroupId = step.FocusGroup != null
                ? (step.Arrangement == "alt" ? built.AltGroups : built.PrimaryGroups)
                    .GetValueOrDefault(step.FocusGroup)
                : null;
            var focusThortId = step.FocusThort != null ? built.Thorts.GetValueOrDefault(step.FocusThort) : null;

            if (engine.AddTripStep(tripId, step.Narration,
                arrangementId: arrangementId,
                focusGroupId: focusGroupId,
                focusThortId: focusThortId,
                name: step.Title,
                framing: step.Framing ?? (focusGroupId != null ? "group" : "wide")))
                stepCount++;
        }

        engine.SetTripPublic(tripId, true);
        return new BuiltJourney(story.Name, tripId, stepCount);
    }
}
