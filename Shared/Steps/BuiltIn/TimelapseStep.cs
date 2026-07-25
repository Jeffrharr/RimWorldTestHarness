using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// The one built-in COMPOSITE: a Timelapse never reaches the executor, because it desugars at load
// time into a SetTime/Wait/Screenshot triple per frame. It implements IStepExpander as well as
// IStepSpec, which is exactly the shape a third-party composite would take.
//
// It still needs a spec (and still appears in KnownTypes) because a Timelapse that FAILED to expand
// is left in place on purpose. Without a registered type it would collect a second, misleading
// "unknown type" error on top of the specific reason its args were rejected.
//
// The residue it declares is the residue its EXPANSION would have had — clock jumps plus hidden-UI
// screenshots — so a malformed Timelapse can never make a scenario look cleaner, and therefore
// cheaper to isolate, than the equivalent valid one.
public sealed class TimelapseStep : IStepSpec, IStepExpander
{
    public string Type => StepArgs.TimelapseType;
    public ScenarioResidue Residue => ScenarioResidue.Clock | ScenarioResidue.ScreenshotMode;
    public bool LiveCallable => false;

    // Args are checked by the expander below, which has to parse them all anyway; validating twice
    // would risk the two checks disagreeing.
    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }

    public void Expand(ScenarioStep step, List<ScenarioStep> into, List<string> errors)
    {
        if (!TimelapseExpander.TryExpand(step.Args, out List<ScenarioStep> frames, out string? error))
        {
            errors.Add($"Timelapse step is invalid and was not expanded: {error}");
            return;
        }

        into.AddRange(frames);
    }
}
