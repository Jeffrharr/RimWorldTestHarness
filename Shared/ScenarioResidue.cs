using System;
using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// What a scenario leaves behind in the shared world after it finishes. This is the thing that makes
// batching several scenarios into one game load a correctness problem rather than a scheduling one:
// scenarios are authored as if they own the world, and back-to-back they do not.
//
// Modelled as flags rather than a bool ("dirty / clean") because the flags differ in how expensive
// they are to undo, and that difference is the whole isolation policy: everything except Map can be
// put back by assigning a few values (see Mod/WorldStateReset.cs), whereas Map cannot be put back at
// all — PlaceThings spawns with WipeMode.Vanish (destroying whatever it replaced), SetTerrain
// repaints ground, and both lift fog of war. Only reloading the save undoes those.
[Flags]
public enum ScenarioResidue
{
    None = 0,

    // Game clock moved (SetSeason / SetTime / FastForward / an expanded Timelapse).
    Clock = 1 << 0,

    // HarnessRuntime.ForcedLatitude set by SetTile, read by Patch_ForcedLatitude for the rest of the
    // process unless cleared.
    Latitude = 1 << 1,

    // A named flag in the mod under test was flipped through FeatureRegistry.
    FeatureFlags = 1 << 2,

    // FastForward raises TimeSpeed to Superfast and never lowers it, so a following scenario would
    // find the colony running at speed while it tries to hold a moment still.
    TimeSpeed = 1 << 3,

    // Camera position/zoom moved by LookAt.
    Camera = 1 << 4,

    // Vanilla's screenshotMode flag, which a hidden-UI Screenshot sets and deliberately does not
    // restore (the capture completes over later frames). Listed separately from Camera because it is
    // a UI flag, and because a following scenario asking for hideUi=false would otherwise silently
    // get a hidden UI: StepExecutor only ever sets the flag, never clears it.
    ScreenshotMode = 1 << 5,

    // Map geometry/terrain/fog changed. The one residue a soft reset cannot undo.
    Map = 1 << 6,

    // A GameCondition (solar flare, eclipse, ...) was registered on the map by StartCondition.
    // Deliberately NOT in SoftResettable: WorldStateReset can put back a handful of values, but it
    // cannot un-register a condition, and a condition still running into the next scenario would
    // silently colour its screenshots. Listed apart from Map because it is world state, not
    // geometry — a reader should not have to conclude that StartCondition moves walls.
    GameConditions = 1 << 7,

    // Map weather was changed by SetWeather. Like GameConditions, not soft-resettable: weather is
    // map state WorldStateReset has no handle on, and rain persisting into the next scenario would
    // silently darken its screenshots. Making it soft-resettable (capture the outgoing weather,
    // transition back) is a reasonable follow-up; reload-required is the conservative starting
    // point, because the failure mode of guessing wrong here is silent cross-contamination.
    Weather = 1 << 8,

    // The map's world tile repointed at a different BiomeDef by SetBiome. Not soft-resettable, and for
    // a stronger reason than Weather: a biome change is not confined to the one field — temperature,
    // plant and animal density, ambient sound and the world-map tile graphic all read off it, and
    // RimWorld caches parts of that. See Shared/Steps/BuiltIn/SetBiomeStep.cs.
    Biome = 1 << 9,

    // A whole additional Map was generated and the game switched to it (LandInOrbit). Listed apart
    // from Map, which describes edits to the map a scenario was handed: this is a different map
    // entirely, on a different PlanetLayer, with the fixture's own colony left untouched underneath
    // it. Reload-only for the obvious reason — nothing un-generates a map — but the dangerous half is
    // the switch rather than the generation: a following scenario that believed itself isolated would
    // open on the orbital platform and measure the wrong world while every step in it reported
    // success.
    NewMap = 1 << 10,

    All = Clock | Latitude | FeatureFlags | TimeSpeed | Camera | ScreenshotMode | Map |
          GameConditions | Weather | Biome | NewMap,
}

// Pure classification of a step list's residue. Kept in Shared with no game types so the whole
// isolation decision is unit-testable offline — the alternative (deciding per-step inside the driver)
// would only be checkable by booting RimWorld, which is exactly the cost this feature exists to cut.
public static class ScenarioResidueAnalyzer
{
    // What Mod/WorldStateReset.Restore is able to put back. Everything not in here needs a reload.
    public const ScenarioResidue SoftResettable =
        ScenarioResidue.Clock | ScenarioResidue.Latitude | ScenarioResidue.FeatureFlags |
        ScenarioResidue.TimeSpeed | ScenarioResidue.Camera | ScenarioResidue.ScreenshotMode;

    // Residue only a save reload can undo. Derived from SoftResettable rather than listed separately
    // so the two can never drift: a flag added to ScenarioResidue and forgotten in SoftResettable
    // lands here automatically, which is the safe direction (an unnecessary reload is slow; a missed
    // one silently lets one scenario contaminate the next).
    public const ScenarioResidue RequiresReload = ScenarioResidue.All & ~SoftResettable;

    public static ScenarioResidue OfSteps(IReadOnlyList<ScenarioStep> steps)
    {
        ScenarioResidue residue = ScenarioResidue.None;
        for (int i = 0; i < steps.Count; i++)
            residue |= OfStep(steps[i].Type);

        return residue;
    }

    public static ScenarioResidue OfScenario(ScenarioSpec scenario) => OfSteps(scenario.Steps);

    // Each step declares its own residue on its registered spec, so this is a lookup rather than the
    // switch it used to be. That switch was a file every new step had to be threaded through, and
    // StartCondition proved the hazard: it shipped without a case here and silently fell to the
    // `default` arm below, reporting that it had dirtied the clock, latitude, camera and map when it
    // touches none of them.
    public static ScenarioResidue OfStep(string stepType)
    {
        // Assume the worst for a step type we don't recognise. An unknown type is already a load
        // error so the suite will fail regardless, but a conservative answer here means a step whose
        // spec failed to register degrades into "reload between everything" (slow but correct)
        // rather than "share the world" (fast and wrong).
        return Steps.StepRegistry.TryGet(stepType, out Steps.IStepSpec? spec) && spec != null
            ? spec.Residue
            : ScenarioResidue.All;
    }

    // Human-readable residue, for the plan lines written to Player.log and the suite report. A run
    // that reloaded four times should be able to say why without anyone re-deriving it.
    public static string Describe(ScenarioResidue residue)
    {
        if (residue == ScenarioResidue.None)
            return "nothing";

        List<string> parts = new List<string>();
        AddIfSet(parts, residue, ScenarioResidue.NewMap, "a new current map");
        AddIfSet(parts, residue, ScenarioResidue.Map, "map");
        AddIfSet(parts, residue, ScenarioResidue.GameConditions, "game conditions");
        AddIfSet(parts, residue, ScenarioResidue.Weather, "weather");
        AddIfSet(parts, residue, ScenarioResidue.Clock, "clock");
        AddIfSet(parts, residue, ScenarioResidue.Latitude, "latitude");
        AddIfSet(parts, residue, ScenarioResidue.FeatureFlags, "feature flags");
        AddIfSet(parts, residue, ScenarioResidue.TimeSpeed, "time speed");
        AddIfSet(parts, residue, ScenarioResidue.Camera, "camera");
        AddIfSet(parts, residue, ScenarioResidue.ScreenshotMode, "screenshot mode");
        return string.Join(", ", parts);
    }

    private static void AddIfSet(List<string> parts, ScenarioResidue residue, ScenarioResidue flag, string label)
    {
        if ((residue & flag) != 0)
            parts.Add(label);
    }
}
