using System;
using System.Collections.Generic;
using RimWorldTestHarness.Mod.Steps;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace RimWorldTestHarness.Mod;

// The result of running a single dev-action against the live game. Deliberately a plain data bag
// with no game types, so BOTH drivers can translate it into their own output shape: the batch
// ScenarioDriver folds it into a ScenarioReport, the live LiveCommandDriver folds it into a
// LiveResponse. Some actions can't complete in the frame they start — a screenshot needs a few
// frames to flush to disk, a FastForward needs real ticks to elapse — so the outcome carries the
// wait the driver must honour before it treats the command as finished.
public sealed class StepOutcome
{
    // Immediate effects the driver should apply / record.
    public float? ForcedLatitude;   // SetTile: driver copies into HarnessRuntime.ForcedLatitude
    public float? ProbeValue;       // Probe: the raw reading (caller decides how to interpret it)
    public string? ScreenshotPath;  // Screenshot: absolute path the PNG is being written to
    public string? Error;           // non-null => the action failed; nothing else is meaningful

    // Post-effect waits the driver must satisfy before the NEXT command runs.
    public int WaitFrames;                  // screenshot flush countdown (frames)
    public bool WaitFastForward;            // true => wait until TicksGame >= FastForwardTargetTicksGame
    public int FastForwardTargetTicksGame;

    public static StepOutcome Fail(string error) => new StepOutcome { Error = error };
}

// The bits of live-game context a step needs that differ between callers. Kept tiny and explicit
// (rather than reaching for Find.* inside StepExecutor) so screenshot output policy stays with the
// caller: batch writes into the run's report folder, the live channel writes into its session dir.
public sealed class StepContext
{
    public Map Map { get; }

    // Maps a screenshot's caller-facing name/arg to the absolute file path to capture to.
    public Func<string, string> ResolveScreenshotPath { get; }

    public StepContext(Map map, Func<string, string> resolveScreenshotPath)
    {
        Map = map;
        ResolveScreenshotPath = resolveScreenshotPath;
    }
}

// The single, shared entry point for running one harness dev-action against the live game, used by
// both the batch ScenarioDriver and the interactive LiveCommandDriver so the two can never drift.
//
// What each action DOES now lives in its own IStepAction (Mod/Steps/), paired with an IStepSpec in
// Shared that carries the game-free half. This used to be a switch every new step had to be added
// to; it is now a registry lookup, which is what lets a contributor add a step without editing any
// file of ours. See DESIGN.md, "Step registration".
public static class StepExecutor
{
    // Runs one action. Any exception (e.g. a malformed numeric arg) is turned into a failed
    // StepOutcome rather than propagating out through the Root_Play.Update postfix that ultimately
    // calls this — a bad scenario/command should surface as an error entry, not crash the frame.
    public static StepOutcome Execute(string actionType, IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        try
        {
            return Dispatch(actionType, args, ctx);
        }
        catch (Exception ex)
        {
            return StepOutcome.Fail($"Action '{actionType}' threw: {ex.Message}");
        }
    }

    // A registered composite (Timelapse) has a spec but deliberately no action: it desugars at load
    // time and should never reach here. One that does failed validation and was left in place on
    // purpose, so it fails the run rather than letting a whole sweep go missing quietly — the
    // specific reason is already in the report via ScenarioSpec.LoadErrors.
    private static StepOutcome Dispatch(string actionType, IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (StepActionRegistry.TryGet(actionType, out IStepAction? action) && action != null)
            return action.Execute(args, ctx);

        if (StepRegistry.TryGet(actionType, out IStepSpec? spec) && spec is IStepExpander)
        {
            return StepOutcome.Fail(
                $"{actionType} step was not expanded — its args are invalid (see the earlier load error)");
        }

        return StepOutcome.Fail($"Unknown action type '{actionType}'");
    }
}
