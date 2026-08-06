using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// The game-free half of the profiling steps. What they DO lives in Mod/Steps/BuiltIn/ProfileActions.cs
// on top of the reflection adapter in Mod/Profiling/DubsAnalyzer.cs; everything a loader, the residue
// analyzer or the suite planner needs is here, where it is testable without a game or a profiler.
//
// WHY THE ANALYZER IS OPTIONAL AND STAYS THAT WAY. Dubs Performance Analyzer is a Steam Workshop mod
// (2038874626), so it cannot be a hard dependency of anything shipped here — and more importantly it
// must NOT be in the load order of ordinary runs, because a profiler changes what those runs measure.
// Every step below therefore reaches the analyzer only through reflection, and reports a SKIP (not an
// error, and never silence) when it is absent. See Runner/run_test.sh's --profiler flag, which is the
// only thing that puts the analyzer in a run's ModsConfig.
//
// RESIDUE. Everything that activates the analyzer declares ScenarioResidue.Profiler, which is not
// soft-resettable: a scenario that profiles forces a save reload before the next one, because
// transplanted method bodies stay transplanted for the life of the process. That cost is the point —
// the alternative is the following scenario's timings being quietly wrong.

// Composite. Desugars at load time into ProfileStart / ProfileMeasure / ProfileStop; see
// ProfileExpander for why the window has to be three steps rather than one.
//
// Like Timelapse and TickLapse it keeps a spec (and stays in KnownTypes) even though it never reaches
// the executor, so a Profile whose args were rejected fails with that specific reason instead of also
// collecting a misleading "unknown type" error.
public sealed class ProfileStep : IStepSpec, IStepExpander
{
    public string Type => StepArgs.ProfileType;

    // The residue its EXPANSION would have had, so a malformed Profile can never make a scenario look
    // cleaner — and therefore cheaper to isolate — than the equivalent valid one.
    public ScenarioResidue Residue => ScenarioResidue.Profiler | ScenarioResidue.TimeSpeed;

    // Never live-callable. The live companion channel promises to be minimally invasive against a real
    // player's colony, and this one rewrites the IL of every patched method in their load.
    public bool LiveCallable => false;

    // Args are checked by the expander, which has to parse them all anyway; validating twice would
    // risk the two checks disagreeing.
    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }

    public void Expand(ScenarioStep step, List<ScenarioStep> into, List<string> errors)
    {
        if (!ProfileExpander.TryExpand(step.Args, out List<ScenarioStep> steps, out string? error))
        {
            errors.Add($"Profile step invalid and not expanded: {error}");
            return;
        }

        into.AddRange(steps);
    }
}

// Activates the analyzer and warms it up. Usable on its own when the window should be "whatever the
// next few steps do" rather than a fixed frame count — pair it with a bare ProfileStop.
public sealed class ProfileStartStep : IStepSpec
{
    public string Type => StepArgs.ProfileStartType;
    public ScenarioResidue Residue => ScenarioResidue.Profiler | ScenarioResidue.TimeSpeed;
    public bool LiveCallable => false;

    private static readonly string[] KnownArgs =
    {
        StepArgs.ProfileEntry,
        StepArgs.ProfileTimeSpeed,
        StepArgs.ProfileWarmupFrames,
    };

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!ArgReader.ValidateKnownArgs(args, KnownArgs, out error))
            return false;

        string entry = ArgReader.ReadString(args, StepArgs.ProfileEntry, ProfileExpander.DefaultEntry);
        if (!ProfileExpander.ValidateEntry(entry, out error))
            return false;

        string timeSpeed = ArgReader.ReadString(args, StepArgs.ProfileTimeSpeed, ProfileExpander.DefaultTimeSpeed);
        if (!ProfileExpander.ValidateTimeSpeed(timeSpeed, out error))
            return false;

        if (!ArgReader.TryReadInt(args, StepArgs.ProfileWarmupFrames, ProfileExpander.DefaultWarmupFrames,
                                  out int warmup, out error))
            return false;

        return ProfileExpander.ValidateWarmup(warmup, out error);
    }
}

// Zeroes the analyzer's counters and opens the measured window. `frames` defaults to 0, meaning "do
// not idle — the steps that follow are the window", which is the whole reason the primitives are
// separately usable.
public sealed class ProfileMeasureStep : IStepSpec
{
    public string Type => StepArgs.ProfileMeasureType;

    // Zeroing counters needs the analyzer already running, so this step never activates anything
    // itself. It still declares Profiler residue: a scenario reaching this step has one active, and a
    // residue that depended on which step in the sequence you looked at would be a trap.
    public ScenarioResidue Residue => ScenarioResidue.Profiler;
    public bool LiveCallable => false;

    private static readonly string[] KnownArgs = { StepArgs.ProfileFrames };

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!ArgReader.ValidateKnownArgs(args, KnownArgs, out error))
            return false;

        if (!ArgReader.TryReadInt(args, StepArgs.ProfileFrames, 0, out int frames, out error))
            return false;

        if (frames < 0)
        {
            error = $"'{StepArgs.ProfileFrames}' must not be negative (got {frames})";
            return false;
        }

        // 0 is legal here (unlike on Profile, where it would mean an empty window); anything positive
        // still has to fit the analyzer's ring buffer.
        if (frames == 0)
        {
            error = null;
            return true;
        }

        return ProfileExpander.ValidateFrames(frames, out error);
    }
}

// Harvests the table and stops profiling.
public sealed class ProfileStopStep : IStepSpec
{
    public string Type => StepArgs.ProfileStopType;
    public ScenarioResidue Residue => ScenarioResidue.Profiler;
    public bool LiveCallable => false;

    private static readonly string[] KnownArgs = { StepArgs.ProfileName, StepArgs.ProfilePrefix };

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!ArgReader.ValidateKnownArgs(args, KnownArgs, out error))
            return false;

        if (!ProfileExpander.ValidateName(ArgReader.ReadString(args, StepArgs.ProfileName, ""), out error))
            return false;

        return ProfileExpander.ValidatePrefix(ArgReader.ReadString(args, StepArgs.ProfilePrefix, ""), out error);
    }
}

// Checks one number out of a harvested table, producing an ordinary ProbeCheckResult so it lands in
// exactly the gate a Probe step lands in. No residue: it reads a table already in the report.
public sealed class ProfileAssertStep : IStepSpec
{
    public string Type => StepArgs.ProfileAssertType;
    public ScenarioResidue Residue => ScenarioResidue.None;
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        ProfileAssertArgs.TryParse(args, out ProfileAssertArgs.Parsed _, out error);
}
