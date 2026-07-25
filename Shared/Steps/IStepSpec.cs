using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps;

// The PURE half of a step type's definition: everything the loader, the validator and the suite
// planner need to know about a step without a live game anywhere in sight.
//
// This interface is the reason adding a step no longer means editing our code. Before it, a new step
// had to be threaded through five to eight central files (StepArgs, StepValidator's KnownStepTypes,
// ScenarioResidueAnalyzer's switch, StepExecutor's Dispatch, LiveCommandDriver's HarnessVerbs, and
// sometimes WorldStateReset) — every contributor's PR editing the same lines as every other's. Now a
// step declares itself in one place and registers from whatever assembly it lives in.
//
// Why this is split from the executing half (Mod/Steps/IStepAction.cs) rather than being one
// interface: Execute needs a live Map, and Shared is netstandard2.0 with no Verse/UnityEngine
// reference precisely so validation and residue stay checkable offline. Merging the two would drag
// the game into the assembly whose whole purpose is not needing it — and the suite isolation
// decision would then only be testable by booting RimWorld, which is the cost this repo exists to
// cut. See DESIGN.md, "Step registration".
public interface IStepSpec
{
    // The step's `type` string in scenario JSON. Must be unique across every registered spec;
    // StepRegistry rejects a collision loudly rather than letting one silently shadow the other.
    string Type { get; }

    // What running this step leaves behind in a shared world, which is what decides whether a suite
    // can run the next scenario in the same load or has to reload the save first. Get this wrong in
    // the "too clean" direction and scenarios silently contaminate each other; see ScenarioResidue.
    ScenarioResidue Residue { get; }

    // Whether the interactive companion channel may run this step against a real player's running
    // colony. Defaults to false for anything new, and should stay false for anything that mutates
    // the world: that channel promises to be minimally invasive, and PlaceThings spawns with
    // WipeMode.Vanish, which would silently bulldoze part of someone's base. Scenario runs are where
    // destructive verbs belong — they load a throwaway fixture and never save it.
    bool LiveCallable { get; }

    // Load-time arg check. Pure: no filesystem, no game state, so a malformed step is reported
    // before the run spends minutes producing frames that show nothing. Returning false with a
    // reason puts the step in ScenarioSpec.LoadErrors; the step is deliberately NOT dropped, so it
    // fails again at execution rather than letting the run quietly verify less than it claimed.
    bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error);
}

// Optional companion for COMPOSITE steps — ones desugared into primitive steps at load time rather
// than executed directly (Timelapse expanding into a SetTime/Wait/Screenshot triple per frame).
//
// Kept as a separate interface rather than a method on IStepSpec because Mono/net481 cannot execute
// C# default-interface-method bodies at runtime, so widening IStepSpec would force every ordinary
// step to implement an Expand it does not have — the same constraint that keeps IProbeMetadata
// separate from IProbe.
public interface IStepExpander
{
    // Appends the primitive steps this one stands for. Anything malformed goes in `errors` and the
    // original step should be left un-expanded by the caller, so the run fails rather than silently
    // losing a whole sweep.
    void Expand(ScenarioStep step, List<ScenarioStep> into, List<string> errors);
}
