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

    All = Clock | Latitude | FeatureFlags | TimeSpeed | Camera | ScreenshotMode | Map,
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

    public static ScenarioResidue OfSteps(IReadOnlyList<ScenarioStep> steps)
    {
        ScenarioResidue residue = ScenarioResidue.None;
        for (int i = 0; i < steps.Count; i++)
            residue |= OfStep(steps[i].Type);

        return residue;
    }

    public static ScenarioResidue OfScenario(ScenarioSpec scenario) => OfSteps(scenario.Steps);

    public static ScenarioResidue OfStep(string stepType)
    {
        switch (stepType)
        {
            case StepArgs.SetTileType:
                return ScenarioResidue.Latitude;
            case StepArgs.SetSeasonType:
            case StepArgs.SetTimeType:
                return ScenarioResidue.Clock;
            case StepArgs.FastForwardType:
                return ScenarioResidue.Clock | ScenarioResidue.TimeSpeed;
            case StepArgs.SetFeatureType:
                return ScenarioResidue.FeatureFlags;
            case StepArgs.LookAtType:
                return ScenarioResidue.Camera;
            case StepArgs.ScreenshotType:
                return ScenarioResidue.ScreenshotMode;
            case StepArgs.PlaceThingsType:
            case StepArgs.SetTerrainType:
                return ScenarioResidue.Map;
            case StepArgs.TimelapseType:
                // Normally desugared before this runs; one surviving here failed validation. Claim the
                // residue its expansion would have had anyway, so a malformed Timelapse can't make a
                // scenario look cleaner than the equivalent valid one.
                return ScenarioResidue.Clock | ScenarioResidue.ScreenshotMode;
            case StepArgs.ProbeType:
            case StepArgs.WaitType:
                // Read-only / idle: genuinely leave nothing behind.
                return ScenarioResidue.None;
            default:
                // Assume the worst for a step type we don't recognise. An unknown type is already a
                // load error so the suite will fail regardless, but a conservative answer here means a
                // future step type added to StepExecutor and forgotten here degrades into "reload
                // between everything" (slow but correct) instead of "share the world" (fast and wrong).
                return ScenarioResidue.All;
        }
    }

    // Human-readable residue, for the plan lines written to Player.log and the suite report. A run
    // that reloaded four times should be able to say why without anyone re-deriving it.
    public static string Describe(ScenarioResidue residue)
    {
        if (residue == ScenarioResidue.None)
            return "nothing";

        List<string> parts = new List<string>();
        AddIfSet(parts, residue, ScenarioResidue.Map, "map");
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
