using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// The two verification channels plus the feature toggle that makes A/B comparison possible. All
// three are live-callable: they are what the companion channel exists to do — read a number, grab a
// frame, flip a flag — and none of them mutates the colony.

// Reads one registered IProbe. The numeric gate; the only step whose result decides Pass.
public sealed class ProbeStep : IStepSpec
{
    public string Type => StepArgs.ProbeType;
    public ScenarioResidue Residue => ScenarioResidue.None;
    public bool LiveCallable => true;

    // Only `pinnedUnder` is checked, and only for spelling. The other three args are read by the
    // driver, which has always parsed them at execution time; validating them here as well would give
    // two checks that can disagree. A misspelled `pinnedUnder`, though, would silently disable the one
    // guardrail the author wrote it down to get — "no-profier" would parse as "any" and gate nothing —
    // so it fails at LOAD, before the run it was supposed to protect starts.
    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        ProbePinning.Validate(
            args.TryGetValue(StepArgs.ProbePinnedUnder, out string? pinnedUnder) ? pinnedUnder : null,
            out error);
}

// ScreenshotMode residue, not None: a hidden-UI capture sets vanilla's screenshotMode flag and
// deliberately does not restore it (the capture completes over later frames), so a following
// scenario asking for hideUi=false would otherwise silently get a hidden UI.
public sealed class ScreenshotStep : IStepSpec
{
    public string Type => StepArgs.ScreenshotType;
    public ScenarioResidue Residue => ScenarioResidue.ScreenshotMode;
    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }
}

// Flips a named flag in the mod under test through FeatureRegistry. The flag belongs to that mod, so
// the residue is FeatureFlags and WorldStateReset puts every registered flag back to its registered
// default between scenarios.
public sealed class SetFeatureStep : IStepSpec
{
    public string Type => StepArgs.SetFeatureType;
    public ScenarioResidue Residue => ScenarioResidue.FeatureFlags;
    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }
}
