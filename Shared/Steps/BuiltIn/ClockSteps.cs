using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// The clock/world steps. Each is the pure half only — what it's called, what it dirties, whether the
// live channel may run it, and how to check its args offline. The half that touches the game lives
// in Mod/Steps/BuiltIn/ClockActions.cs.
//
// These carry no arg validation because they never had any: their args are parsed at execution and a
// malformed one surfaces as a step error. Tightening that is a behaviour change (it would start
// failing scenarios at load that currently fail at execution), so it is deliberately left alone
// here — this change is a refactor, not a new gate.

// Overrides latitude in place via Patch_ForcedLatitude rather than regenerating the landing tile.
public sealed class SetTileStep : IStepSpec
{
    public string Type => StepArgs.SetTileType;
    public ScenarioResidue Residue => ScenarioResidue.Latitude;
    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }
}

public sealed class SetSeasonStep : IStepSpec
{
    public string Type => StepArgs.SetSeasonType;
    public ScenarioResidue Residue => ScenarioResidue.Clock;
    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }
}

public sealed class SetTimeStep : IStepSpec
{
    public string Type => StepArgs.SetTimeType;
    public ScenarioResidue Residue => ScenarioResidue.Clock;
    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }
}

// AdvanceTime — move the clock FORWARD by a duration, rather than to an hour of the current day.
//
// WHY BOTH EXIST. SetTime is absolute and pins the day: it reads the current dayOfYear and jumps to
// the requested hour WITHIN it. That is what you want to stage a moment ("make it 3am"), and it is
// quietly wrong for a sequence. Asking for 00:00 right after 23:45 does not step 15 minutes forward,
// it rewinds the clock almost a full day, back to the start of the same day.
//
// Nothing that depends only on hour-of-day notices — the sun is at the same angle at 00:00 whichever
// day it is — so the bug hides in plain sight. What does notice is anything driven by ABSOLUTE time:
// a lunar cycle advances ~50 minutes of moonrise per day, so a mod rendering moonlight sees the moon
// jump a full day backwards, and its shadows visibly swing to a new direction. That is exactly how
// this was found: a day-long timelapse of a lighting mod changed shadow direction at its midpoint,
// on the one frame that crossed midnight.
//
// AdvanceTime cannot express that mistake. It adds a duration to the clock, so a sweep built from it
// is monotonic by construction and rolls into the next day the way the game itself would.
//
// It is a JUMP, not simulation — the same as SetTime and unlike FastForward, which runs real ticks
// so the world actually lives through them. Advancing 6 hours here does not grow crops or burn fuel.
public sealed class AdvanceTimeStep : IStepSpec
{
    public string Type => StepArgs.AdvanceTimeType;
    public ScenarioResidue Residue => ScenarioResidue.Clock;
    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!ArgReader.ValidateKnownArgs(args, new[] { StepArgs.AdvanceTimeHours }, out error))
            return false;
        if (!ArgReader.TryReadDouble(args, StepArgs.AdvanceTimeHours, 0, out double hours, out error))
            return false;

        // Zero is rejected as well as negative. A zero-hour advance is a frame that captures the same
        // instant twice, which in a timelapse reads as a stutter rather than an error.
        if (hours <= 0)
        {
            error = $"'{StepArgs.AdvanceTimeHours}' must be greater than 0 (got {ArgReader.Format(hours)}); " +
                    $"to go to a specific hour use '{StepArgs.SetTimeType}' instead";
            return false;
        }

        error = null;
        return true;
    }
}

// TimeSpeed as well as Clock: FastForward raises the game to Superfast and never lowers it, so a
// following scenario would find the colony running while it tries to hold a moment still.
public sealed class FastForwardStep : IStepSpec
{
    public string Type => StepArgs.FastForwardType;
    public ScenarioResidue Residue => ScenarioResidue.Clock | ScenarioResidue.TimeSpeed;
    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }
}

// Burns rendered frames, not game ticks, so it genuinely leaves nothing behind. Not live-callable:
// idling is a batch-sequencing concept, and the companion channel has nothing to sequence.
public sealed class WaitStep : IStepSpec
{
    public string Type => StepArgs.WaitType;
    public ScenarioResidue Residue => ScenarioResidue.None;
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }
}
