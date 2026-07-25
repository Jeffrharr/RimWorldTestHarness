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
