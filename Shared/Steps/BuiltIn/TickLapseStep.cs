using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// The second built-in COMPOSITE, alongside Timelapse: TickLapse never reaches the executor, because
// it desugars at load time into an AdvanceTicks/Wait/Screenshot triple per frame. See
// TickLapseExpander for why a tick sweep is a different instrument from an hour sweep rather than
// the same one at a finer setting.
//
// Like Timelapse it still needs a spec (and still appears in KnownTypes) because a TickLapse that
// FAILED to expand is left in place on purpose. Without a registered type it would collect a second,
// misleading "unknown type" error on top of the specific reason its args were rejected.
//
// Residue declares the residue its EXPANSION would have had — clock jumps plus hidden-UI screenshots
// — so a malformed TickLapse can never make a scenario look cleaner, and therefore cheaper to
// isolate, than the equivalent valid one.
public sealed class TickLapseStep : IStepSpec, IStepExpander
{
    public string Type => StepArgs.TickLapseType;
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
        if (!TickLapseExpander.TryExpand(step.Args, out List<ScenarioStep> frames, out string? error))
        {
            errors.Add($"TickLapse step invalid and not expanded: {error}");
            return;
        }

        into.AddRange(frames);
    }
}
