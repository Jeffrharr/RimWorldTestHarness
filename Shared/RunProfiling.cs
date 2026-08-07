using System.Globalization;

namespace RimWorldTestHarness.Shared;

// The pure core of RUN-LEVEL profiling: profiling as a property of the run rather than of a scenario.
//
// WHY THIS REPLACED THE OPT-IN STEP AS THE PRIMARY SHAPE. The first cut of this feature was a Profile
// step a scenario asked for. It worked, and it cost a save reload per scenario that used it, because
// activating the analyzer mid-run leaves ScenarioResidue.Profiler behind and that residue is not
// soft-resettable (Analyzer.Profiling.MethodTransplanting rewrites method bodies and nothing puts them
// back). A ten-scenario suite in which every scenario wanted a table would therefore have gone from one
// boot to nine mid-suite reloads — the exact cost batching exists to avoid.
//
// Starting the analyzer ONCE, right after the first map load and before the first scenario's first
// step, removes the residue entirely: every scenario in the run is instrumented identically, from
// before any of them ran, so no scenario can contaminate the next one's numbers. Resetting the
// analyzer's counters at each scenario's first step and harvesting at its last turns "one profiler for
// the run" into "one table per scenario", for free and with no scenario JSON.
//
// WHAT THIS COSTS, AND WHY IT IS STILL ON BY DEFAULT. Every timing number in a profiled run — ordinary
// Probe steps included — is measured through an instrumented build. That is a real hazard and it is
// why ProbePinning exists below: a probe can record which mode its expected value was pinned under and
// fail rather than silently compare across modes. Default-on is the right trade because the numbers
// profiling produces (call counts, worst-frame spikes, per-patch attribution) are ones no probe can
// express at all, and a feature nobody remembers to switch on is a feature nobody uses.
public static class RunProfiling
{
    // Set by Runner/run_test.sh to "1" or "0". Unset is treated as off, so a game launched by hand —
    // or by an older copy of the runner — behaves exactly as it did before this existed.
    public const string EnvEnabled = "RWTH_PROFILE";

    // A reason the RUNNER ruled profiling out before the game was launched (today: the analyzer is not
    // installed anywhere it can see). Carried in rather than re-derived in-game because the runner is
    // the thing that actually scanned the disk, and "it is not installed" and "it is installed but
    // RimWorld did not load it" are different problems with different fixes.
    public const string EnvSkipReason = "RWTH_PROFILE_SKIP";

    // The name every run-level table gets in the report. Fixed rather than derived from the scenario
    // name so a ProfileAssert can refer to it without knowing which scenario it is sitting in, and so
    // a diff between two runs' reports lines the tables up by key.
    public const string TableName = "scenario";

    // No prefix filter: the run-level table is the whole load's Harmony patches, because nothing in a
    // run-level table's provenance says which mod the reader cares about. That is affordable only
    // because Runner/run_test.sh writes a MINIMAL ModsConfig — Core, the DLCs, Harmony, the mods under
    // test and the harness — so the analyzer's Harmony-patches entry covers a handful of methods rather
    // than a whole player modlist. A live run of this repo's own scenarios produced two rows.
    // MaxRows below is the belt to that braces.
    public const string NoPrefix = "";

    // Rows kept in a run-level table, most expensive first. A cap rather than no cap because the row
    // count is ultimately whatever the mods under test patch, and a suite of ten scenarios each
    // carrying a few hundred rows would turn a report into something nobody opens. Totals are computed
    // over EVERY matched row before this cap is applied (see ProfileMath.Build) — a truncated table
    // whose totals silently described only the rows that survived would be precisely the plausible
    // wrong number this whole feature is built to avoid.
    public const int MaxRows = 200;

    // Below this many frames a per-frame mean is noise rather than a measurement: one expensive frame
    // in a window of five moves the mean by 20%, and the reader has no way to see that from the table.
    // 30 frames is a fraction of a second of wall clock and is the same figure ProfileExpander uses for
    // warmup, i.e. the span already judged long enough for every instrumented method to have run once.
    //
    // A window this short is reported as a SKIP with the count in it, never as a table. That is the
    // whole point: a scenario of three instantaneous steps takes three frames, and three frames of
    // near-zero readings would render as "this mod is free".
    public const int MinimumFrames = 30;

    // Decided before the analyzer is touched, from what the runner and the loader already know. Returns
    // null when profiling should proceed, or the reason it will not.
    //
    // Up-front rather than at harvest wherever possible, because a reason produced before any
    // measurement can name the CAUSE ("not installed", "this scenario has no steps") while a reason
    // produced afterwards can only describe the symptom ("no frames were recorded"). Both exist; this
    // one is preferred and runs first.
    public static string? BeforeStartSkipReason(bool requested, string? runnerSkipReason, bool analyzerLoaded)
    {
        if (!requested)
            return null;

        // The runner's own verdict wins: it looked at the filesystem, we cannot.
        if (!string.IsNullOrWhiteSpace(runnerSkipReason))
            return runnerSkipReason;

        if (!analyzerLoaded)
            return NotLoadedReason;

        return null;
    }

    // Worded to be true in BOTH situations it appears in, because the same sentence is reported by an
    // explicit Profile step in a --no-profiler run (where the analyzer's absence is exactly what was
    // asked for) and by a run that wanted it and did not get it.
    public const string NotLoadedReason =
        "Dubs Performance Analyzer is not loaded, so nothing was profiled. It is an optional Workshop " +
        "mod (2038874626) that Runner/run_test.sh adds to a run's mod list by default — this run either " +
        "passed --no-profiler, or the analyzer is not installed (the runner prints which).";

    // Everything a run-level window can turn out to be worth nothing for, classified AFTER the window
    // closed. Returns null when the harvested numbers are worth writing down.
    //
    // Every branch here exists because the alternative is a table of zeroes, and a table of zeroes is
    // not a null result — it is a number that looks like a measurement, means "nothing was measured",
    // and reads as "this mod is free". That is the same failure as profiling a paused colony, and it is
    // worse than an error because it survives review.
    public static string? AfterWindowSkipReason(
        bool gameReached,
        bool windowOpened,
        string? startError,
        int measuredFrames,
        int methodsThatRan)
    {
        // Checked first because it subsumes the rest: with no interactive game there were no frames, no
        // window and nothing to instrument. This is the shape a run made entirely of XML-patch checks
        // has — verifying defs without ever reaching a map is a normal way to use the harness, not an
        // edge case, and such a run must say "not profiled, and here is why" rather than report zeroes.
        if (!gameReached)
        {
            return "the game never became interactive during this run — no map had loaded when it " +
                   "finished — so the profiler was never started and nothing was measured.";
        }

        if (startError != null)
            return $"the profiler could not be started: {startError}";

        // A scenario whose step list is empty (or which skipped out at its first step) never opened a
        // window. Harvesting anyway would report the PREVIOUS scenario's frames under this scenario's
        // name, which is worse than reporting nothing.
        if (!windowOpened)
        {
            return "no measurement window was opened for this scenario — it ran no steps, so there " +
                   "were no frames of its own to attribute anything to.";
        }

        if (measuredFrames <= 0)
        {
            return "Dubs Performance Analyzer recorded no frames during this scenario. Its per-frame " +
                   "cycle runs from Root_Play.Update, so a scenario that elapsed entirely inside a long " +
                   "event (a load, a map regen) measures nothing.";
        }

        if (measuredFrames < MinimumFrames)
        {
            return $"only {Count(measuredFrames)} elapsed during this scenario, fewer than the " +
                   $"{MinimumFrames} a per-frame mean needs to mean anything. A table over this window " +
                   "would be one frame's noise wearing an average's clothes. Add a Wait step, or use " +
                   "an explicit Profile step with a frame count if you want this scenario measured.";
        }

        if (methodsThatRan <= 0)
        {
            return "no instrumented method ran during this scenario, so every row would be zero. " +
                   "Either no Harmony-patched code was reached, or the analyzer failed to instrument " +
                   "the load (check its own log output).";
        }

        return null;
    }

    private static string Count(int frames) =>
        frames == 1 ? "1 frame" : $"{frames.ToString(CultureInfo.InvariantCulture)} frames";

    // Recorded INTO a table whose window was not entirely unpaused. Run-level profiling deliberately
    // does NOT force a game speed the way an explicit ProfileStart does: it must not change what the
    // scenarios it wraps around actually do. The cost of that restraint is that a scenario which jumped
    // the clock leaves the colony paused, and a paused colony runs no ticks — so every tick-driven
    // patch reads as free while render-path patches are measured normally.
    //
    // Disclosed rather than skipped on, because half a measurement is still a measurement: the render
    // rows are real. A skip would throw them away; a silent table would let someone conclude a
    // tick-driven hook costs nothing.
    public static string PausedNote(int pausedFrames, int sampledFrames) =>
        $"The game was PAUSED for {pausedFrames} of {sampledFrames} frames in this window. No ticks ran " +
        "during those frames, so tick-driven patches are absent or near-zero here through no merit of " +
        "their own. Render-path costs are unaffected. Run-level profiling does not force a game speed " +
        "— an explicit Profile step's timeSpeed arg does.";

    // Recorded when the row cap dropped rows. The totals are still whole-table (ProfileMath computes
    // them before truncating), and saying so is the difference between a reader trusting them and a
    // reader assuming they match the visible rows.
    public static string TruncationNote(int shown, int matched) =>
        $"Showing the {shown} most expensive of {matched} rows by AvgMsPerFrame. Totals cover all " +
        $"{matched}, not just the ones listed.";

    // Recorded when an explicit Profile/ProfileMeasure step zeroed the analyzer's counters part-way
    // through the scenario. There is one set of counters in the analyzer, so an explicit window and the
    // run-level window share them: the run-level table then describes only the span since that reset.
    // MeasuredFrames is honest about the length either way, but nothing else in the table would say why
    // it is shorter than the scenario.
    public static readonly string CounterResetNote =
        "An explicit Profile/ProfileMeasure step zeroed the analyzer's counters part-way through this " +
        "scenario. Run-level and explicit windows share one set of counters, so this table covers only " +
        "the span since that reset, not the whole scenario.";
}

// Which profiling mode a pinned number was measured under — the guardrail against the one failure
// default-on profiling makes likely.
//
// THE FAILURE. A probe's expectedValue is pinned by running the scenario and writing down what it read.
// Do that in a profiled run and the number carries the analyzer's instrumentation overhead; compare it
// later against an unprofiled run and the check fails (or passes) for a reason that has nothing to do
// with the code under test. The reverse is just as bad and more likely now that profiling is the
// default: a value pinned before this change, re-checked by a default run today, is being compared
// across modes with nothing saying so.
//
// A `Profiled: true` marker in the report is the minimum mitigation, and it is a weak one — it relies
// on a human noticing a line of output, which is the same weakness as relying on someone noticing
// SKIPPED. So a Probe step may additionally record WHICH mode its expected value came from, and a
// mismatch is an error on the scenario rather than a note in the margin. Opt-in per probe (`any` is
// the default) because most probes read state, not time, and gating those on the profiler's presence
// would be noise that teaches people to switch the gate off.
public static class ProbePinning
{
    // The default: this number does not care. Correct for every probe that reads state — a season, a
    // latitude, a glow level — which is most of them.
    public const string Any = "any";

    // "I pinned this while the profiler was loaded." Fails in an unprofiled run.
    public const string Profiler = "profiler";

    // "I pinned this with the profiler out of the load." Fails in a profiled run — which, since
    // profiling is now on by default, is the case this constant mostly exists to catch.
    public const string NoProfiler = "no-profiler";

    public static readonly string[] Known = { Any, Profiler, NoProfiler };

    public static bool Validate(string? raw, out string? error)
    {
        if (string.IsNullOrEmpty(raw) || System.Array.IndexOf(Known, raw) >= 0)
        {
            error = null;
            return true;
        }

        error = $"unknown '{StepArgs.ProbePinnedUnder}' value '{raw}' " +
                $"(expected one of: {string.Join(", ", Known)})";
        return false;
    }

    // The mismatch message, or null when the run's mode satisfies what the value was pinned under.
    // Returns the reason rather than a bool so the caller has nothing to phrase and the fix — a runner
    // flag — travels with the complaint.
    public static string? Mismatch(string? pinnedUnder, bool runProfiled, string probeName)
    {
        if (string.IsNullOrEmpty(pinnedUnder) || pinnedUnder == Any)
            return null;

        bool wantsProfiler = pinnedUnder == Profiler;
        if (wantsProfiler == runProfiled)
            return null;

        return wantsProfiler
            ? $"probe '{probeName}' declares its expectedValue was pinned under the profiler " +
              $"({StepArgs.ProbePinnedUnder}={Profiler}), but this run is not profiled. Its number was " +
              "measured through an instrumented build and is not comparable with this one — drop " +
              "--no-profiler, or re-pin the value."
            : $"probe '{probeName}' declares its expectedValue was pinned WITHOUT the profiler " +
              $"({StepArgs.ProbePinnedUnder}={NoProfiler}), but this run is profiled. Dubs Performance " +
              "Analyzer rewrites the body of every Harmony-patched method in the load, so this run's " +
              "reading is not comparable with the pinned one — re-run with --no-profiler, or re-pin " +
              "the value under the profiler.";
    }
}
