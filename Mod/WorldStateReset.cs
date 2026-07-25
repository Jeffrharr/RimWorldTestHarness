using System;
using RimWorld;
using RimWorldTestHarness.Mod.Features;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod;

// The pristine, just-loaded values a soft reset restores. Captured every time the driver reaches
// ProgramState.Playing — on the first load and again after every mid-suite reload — so it always
// describes the world as the save defines it, not as some earlier scenario left it.
public sealed class WorldBaseline
{
    public int TicksGame { get; set; }
    public TimeSpeed TimeSpeed { get; set; }
    public IntVec3 CameraCell { get; set; }
    public float CameraRootSize { get; set; }
    public bool HasCamera { get; set; }
}

// The Verse-touching half of scenario isolation: undo the residue a soft reset CAN undo, so that
// scenarios which only read the world (probe, screenshot, move the clock) can share one game load
// without a reload between them.
//
// Deliberately paired with Shared/ScenarioResidue.cs: ScenarioResidueAnalyzer.SoftResettable lists
// exactly the flags this class handles, and SuitePlanner refuses to soft-reset anything else. If a
// future step type dirties something new, the residue flag and the restore here have to be added
// together — the planner's conservative default (an unknown step type dirties everything, forcing a
// reload) is what keeps a half-done addition slow rather than wrong.
public static class WorldStateReset
{
    // HasCamera is false only if Capture ran without a CameraDriver, which shouldn't happen at
    // ProgramState.Playing — but recording it beats restoring the camera to a default (0,0) cell if it
    // ever does.
    public static WorldBaseline Capture()
    {
        WorldBaseline baseline = new WorldBaseline
        {
            TicksGame = Find.TickManager.TicksGame,
            TimeSpeed = Find.TickManager.CurTimeSpeed,
        };

        CameraDriver? camera = Find.CameraDriver;
        if (camera != null)
        {
            baseline.CameraCell = camera.MapPosition;
            baseline.CameraRootSize = camera.RootSize;
            baseline.HasCamera = true;
        }

        return baseline;
    }

    // Returns null on success, or a reason string. Errors are returned rather than thrown because the
    // caller is inside a Root_Play.Update postfix: a throw there aborts the frame's postfix, and since
    // the driver would retry the same reset next frame it would hang rather than crash. A named suite
    // error is a far better outcome — and FeatureRegistry setters belong to the mod under test, so
    // they are somebody else's code that can throw.
    public static string? Restore(WorldBaseline baseline)
    {
        try
        {
            RestoreCore(baseline);
            return null;
        }
        catch (Exception ex)
        {
            return $"soft reset between scenarios threw: {ex.Message}";
        }
    }

    private static void RestoreCore(WorldBaseline baseline)
    {
        // Latitude first: it is a harness flag rather than game state, so clearing it can't be
        // invalidated by anything below.
        HarnessRuntime.ForcedLatitude = null;

        // The clock goes back to the save's own tick, not to zero. SetSeason/SetTime jump absolutely,
        // so a scenario that sets what it needs is unaffected either way — but one that only sets the
        // hour would otherwise inherit the previous scenario's day.
        Find.TickManager.DebugSetTicksGame(baseline.TicksGame);
        Find.TickManager.CurTimeSpeed = baseline.TimeSpeed;

        FeatureRegistry.ResetAll();

        RestoreCamera(baseline);

        // StepExecutor's Screenshot only ever SETS screenshot mode (the capture finishes over later
        // frames, so it can't restore it itself). Without clearing it here, a scenario asking for
        // hideUi=false after one that asked for true would silently get a hidden UI — a wrong image on
        // a green run.
        HarnessDebugActions.SetScreenshotMode(false);
    }

    private static void RestoreCamera(WorldBaseline baseline)
    {
        CameraDriver? camera = Find.CameraDriver;
        if (!baseline.HasCamera || camera == null)
            return;

        camera.JumpToCurrentMapLoc(baseline.CameraCell);
        camera.SetRootSize(baseline.CameraRootSize);
    }

    // Describes what Restore does, for the plan lines in the suite report. Kept next to the code it
    // describes so the two can't drift.
    public static string Describe() =>
        ScenarioResidueAnalyzer.Describe(ScenarioResidueAnalyzer.SoftResettable);
}
