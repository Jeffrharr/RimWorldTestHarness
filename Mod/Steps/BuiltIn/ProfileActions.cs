using System.Collections.Generic;
using RimWorldTestHarness.Mod.Profiling;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of the profiling steps. See Shared/Steps/BuiltIn/ProfileSteps.cs for the
// specs and Mod/Profiling/DubsAnalyzer.cs for the adapter; everything here is sequencing plus the two
// pieces of live state a profiling window needs (the game speed, and where the harvested table goes).

// Activates the analyzer, sets the speed the window will run at, and burns the warmup frames.
public sealed class ProfileStartAction : IStepAction
{
    public string Type => StepArgs.ProfileStartType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        // SKIP, not fail, and for the same reason LandInOrbit skips without Odyssey: an install
        // without the analyzer cannot be made to run this scenario by fixing any mod. The driver
        // abandons the rest of the scenario and reports it SKIPPED — which is the loud no-op the
        // feature needs, as opposed to a green run that silently profiled nothing.
        if (!DubsAnalyzer.IsLoaded)
            return StepOutcome.Skip(DubsAnalyzer.NotLoadedReason);

        if (!TrySetTimeSpeed(args, out string? speedError))
            return StepOutcome.Fail(speedError!);

        string? error = DubsAnalyzer.Start();
        if (error != null)
            return StepOutcome.Fail(error);

        int warmup = ReadInt(args, StepArgs.ProfileWarmupFrames, ProfileExpander.DefaultWarmupFrames);
        Log.Message($"RWTH: profiling started (warming up for {warmup} frames)");
        return new StepOutcome { WaitFrames = warmup };
    }

    // Held for the whole window rather than restored afterwards: the step declares
    // ScenarioResidue.TimeSpeed for exactly that, and restoring here would mean the harvest ran at a
    // different speed from the frames it describes.
    //
    // The default is "normal" rather than "leave it alone" because a scenario that jumped the clock
    // leaves the game PAUSED, and profiling a paused colony records a load of tick-driven patches that
    // never fire — a table of near-zeroes that reads as "this mod is free".
    private static bool TrySetTimeSpeed(IReadOnlyDictionary<string, string> args, out string? error)
    {
        string raw = ArgReaderCompat.ReadString(args, StepArgs.ProfileTimeSpeed, ProfileExpander.DefaultTimeSpeed);
        if (!ProfileExpander.ValidateTimeSpeed(raw, out error))
            return false;

        Find.TickManager.CurTimeSpeed = ToTimeSpeed(raw);
        error = null;
        return true;
    }

    private static TimeSpeed ToTimeSpeed(string raw)
    {
        switch (raw)
        {
            case "paused": return TimeSpeed.Paused;
            case "fast": return TimeSpeed.Fast;
            case "superfast": return TimeSpeed.Superfast;
            case "ultrafast": return TimeSpeed.Ultrafast;
            default: return TimeSpeed.Normal;
        }
    }

    // Invariant culture, matching Shared/ArgReader: a scenario file is data that has to read the same
    // on any machine, and these values were already validated at load through exactly that parser.
    internal static int ReadInt(IReadOnlyDictionary<string, string> args, string key, int fallback) =>
        args.TryGetValue(key, out string? raw) &&
        int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                     System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
}

// Zeroes the counters and holds the run for the measured frames. A `frames` of 0 means "do not idle" —
// the steps that follow are the window, which is how a scenario profiles a screenshot sweep or a
// clock jump rather than a fixed span of idle frames.
public sealed class ProfileMeasureAction : IStepAction
{
    public string Type => StepArgs.ProfileMeasureType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!DubsAnalyzer.IsLoaded)
            return StepOutcome.Skip(DubsAnalyzer.NotLoadedReason);

        int frames = ProfileStartAction.ReadInt(args, StepArgs.ProfileFrames, 0);

        string? error = DubsAnalyzer.BeginWindow(frames);
        if (error != null)
            return StepOutcome.Fail(error);

        Log.Message($"RWTH: profiling window open ({(frames > 0 ? frames + " frames" : "until ProfileStop")})");
        return new StepOutcome { WaitFrames = frames };
    }
}

// Harvests the window into a ProfileTable and stops profiling.
public sealed class ProfileStopAction : IStepAction
{
    public string Type => StepArgs.ProfileStopType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!DubsAnalyzer.IsLoaded)
            return StepOutcome.Skip(DubsAnalyzer.NotLoadedReason);

        string name = ArgReaderCompat.ReadString(args, StepArgs.ProfileName, "");
        string prefix = ArgReaderCompat.ReadString(args, StepArgs.ProfilePrefix, "");

        // Two tables under one name would make a ProfileAssert's target depend on step order.
        if (ProfileResults.Contains(name))
            return StepOutcome.Fail($"a profile table named '{name}' was already recorded in this scenario");

        // Harvest WITHOUT stopping when the run itself is profiling: the analyzer belongs to the run,
        // and tearing it down here would leave every later scenario uninstrumented while the report
        // still said the run was profiled. When nothing else owns it, this step stops it as it always
        // did.
        DubsAnalyzer.Harvest harvest = RunProfiler.Active ? DubsAnalyzer.ReadWindow() : DubsAnalyzer.Stop();
        if (harvest.Error != null)
            return StepOutcome.Fail(harvest.Error);

        // An explicit window that measured nothing is a step FAILURE, not a quiet empty table: this
        // scenario asked to measure and got nothing back. The classification is shared with run-level
        // profiling (which reports the same conditions as a skip, because nobody asked) so the two can
        // never disagree about what "measured nothing" means.
        string? empty = RunProfiling.AfterWindowSkipReason(
            gameReached: true, windowOpened: true, startError: null,
            harvest.MeasuredFrames, harvest.Samples.Count);
        if (empty != null)
            return StepOutcome.Fail(empty);

        ProfileTable table = ProfileMath.Build(
            name, ProfileExpander.DefaultEntry, prefix,
            harvest.RequestedFrames, harvest.MeasuredFrames, harvest.FrameMs, harvest.Samples);

        // A filter that matched nothing is an ERROR, not an empty table. An empty table is
        // indistinguishable from "this mod costs nothing", and the overwhelmingly likely cause is a
        // renamed namespace or a mod that was not in the run's ModsConfig at all — i.e. a scenario
        // that verified nothing while reporting success.
        if (table.Rows.Count == 0)
        {
            return StepOutcome.Fail(
                $"no profiled method's label starts with '{prefix}' " +
                $"({table.RowsBeforeFilter} methods ran during the window). Check the prefix against " +
                "the analyzer's \"Namespace.Type:Method(params)\" labels, and that the mod is in this run's mod list.");
        }

        ProfileResults.Store(table);
        Log.Message($"RWTH: profile '{name}': {ProfileMetrics.Summarize(table)}");

        return new StepOutcome { ProfileTable = table };
    }
}

// Checks one number out of a harvested table, producing an ordinary ProbeCheckResult so it gates the
// run through exactly the path a Probe step does.
public sealed class ProfileAssertAction : IStepAction
{
    public string Type => StepArgs.ProfileAssertType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!ProfileAssertArgs.TryParse(args, out ProfileAssertArgs.Parsed parsed, out string? error))
            return StepOutcome.Fail(error!);

        if (!ProfileResults.TryGet(parsed.Table, out ProfileTable? table) || table == null)
        {
            return StepOutcome.Fail(
                $"no profile table named '{parsed.Table}' in this scenario " +
                $"(recorded: {string.Join(", ", ProfileResults.Names)})");
        }

        if (!ProfileMetrics.TryRead(table, parsed.Label, parsed.Metric, out double actual, out string? readError))
            return StepOutcome.Fail(readError!);

        return new StepOutcome
        {
            ProbeCheck = ReportComparer.Check(
                ProfileAssertArgs.CheckName(parsed.Table, parsed.Label, parsed.Metric),
                actual, parsed.Bound.Expected, parsed.Bound.Tolerance, parsed.Bound.Comparison),
        };
    }
}

// ArgReader lives in Shared and is `internal` to it, so the Mod side cannot call it. These steps only
// need the absent-means-default read (their numeric args were already validated at load), and one
// three-line helper beats widening ArgReader's visibility and inviting the Mod side to re-validate
// what Shared already checked.
internal static class ArgReaderCompat
{
    public static string ReadString(IReadOnlyDictionary<string, string> args, string key, string fallback) =>
        args.TryGetValue(key, out string? raw) ? raw : fallback;
}
