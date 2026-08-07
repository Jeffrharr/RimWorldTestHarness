using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// One profiler's raw readings for a measurement window, exactly as Dubs Performance Analyzer holds
// them — no derived numbers, no rounding, no filtering.
//
// This is the SEAM between the reflection adapter that talks to the analyzer (Mod/Profiling/) and the
// arithmetic that turns its numbers into something a scenario can assert on (ProfileMath). Everything
// interesting about that arithmetic is a division that can be wrong in a way nobody notices — a mean
// over a window that turns out to be a different length than you thought, a percentage of a
// denominator you didn't record — so it lives in Shared where it is unit-testable without a game.
//
// Field-for-field this mirrors Analyzer.Profiling.Profiler.CollectStatistics's out-parameters. Doubles
// throughout, including the two counts, because that is what the analyzer hands back (its `calls` and
// `maxCalls` are floats) and silently truncating someone else's float here would be a second place for
// the number to change shape.
public struct ProfileSample
{
    // The analyzer's own key for the profiled method: "Namespace.Type:Method(params)" (see
    // Analyzer.Profiling.Utility.GetSignature). For the Harmony-patches entry the type is the PATCH's
    // declaring type, which is what makes a `prefix` of the mod's root namespace a usable filter.
    public string Label;

    // Mean milliseconds this method cost PER FRAME over the window — total divided by the number of
    // recorded frames, not per call. A method called 30 times in one frame contributes the sum of
    // those 30 calls to that frame's figure.
    public double AverageMs;

    // The worst single frame in the window. The number that catches a dropped frame an average hides;
    // see the class comment on ProfileTable.
    public double MaxMs;

    // Milliseconds summed over every recorded frame in the window.
    public double TotalMs;

    // Calls summed over every recorded frame in the window.
    public double Calls;

    // The most calls in any single frame of the window. The only shape information the analyzer gives
    // us for free, and the closest thing available to a distribution behind the mean.
    public double MaxCallsPerFrame;
}

// One profiled method's line in a report's profile table: the raw sample plus every number a scenario
// might want to assert on, computed once here rather than by whoever reads the JSON.
public sealed class ProfileRow
{
    public string Label { get; set; } = "";

    public double AvgMsPerFrame { get; set; }

    public double MaxMsPerFrame { get; set; }

    public double TotalMs { get; set; }

    public double Calls { get; set; }

    // Calls divided by the number of frames measured. THE number this whole feature exists for: a
    // per-call timing probe cannot tell you that a hook started firing twice as often after a
    // refactor, and this can.
    public double CallsPerFrame { get; set; }

    public double MaxCallsPerFrame { get; set; }

    // TotalMs / Calls, in microseconds. Read this with the caveat in ProfileTable.Notes: a patch that
    // early-returns on most invocations averages its expensive calls together with its trivial ones,
    // so the real cost of the calls that do work is HIGHER than this says.
    public double AvgUsPerCall { get; set; }

    // AvgMsPerFrame as a share of ProfileTable.FrameMs. Recorded next to that denominator on purpose —
    // see ProfileTable.FrameMs for why the bare percentage is the most misreadable number here.
    public double PercentOfFrame { get; set; }

    // AvgMsPerFrame as a share of a 16.67 ms frame. The percentage a human actually means when they
    // ask "how much of the frame is this costing", because 60 fps is the budget everyone has in mind
    // and the machine's current framerate is not.
    public double PercentOfSixtyFpsBudget { get; set; }
}

// The per-patch cost table a Profile step harvests, written into the run's JSON report so it can be
// asserted (ProfileAssert) and diffed between builds like any other recorded number.
//
// WHY THIS EXISTS ALONGSIDE PROBES. A probe times ONE call of a hot path. It never asks how often that
// call happens, and the call count is frequently the more interesting number: two patches costing the
// same per call but firing 3,700 and 7,400 times a window are telling you something about where they
// are hooked that no per-call timing can. The other thing a probe cannot see is the shape of the cost
// — an average of 0.45 ms with a worst frame of 3.0 ms is a dropped frame that the average hides
// completely, and a rolling-refresh design that exists to avoid exactly that spike can only be shown
// to work by measuring the worst frame.
public sealed class ProfileTable
{
    // The scenario author's name for this window, and the handle a ProfileAssert step names. Scoped to
    // the scenario, so two scenarios may both have a "startup" table without colliding.
    public string Name { get; set; } = "";

    // Which analyzer entry was profiled. Only "harmony" (every non-analyzer Harmony patch in the load)
    // is supported today; recorded anyway so a table read a year from now says what it measured.
    public string Entry { get; set; } = "";

    // The label prefix rows were filtered to, or "" for unfiltered. Recorded because a table showing
    // three rows means something very different depending on whether it was filtered to one mod.
    public string Prefix { get; set; } = "";

    // Frames the step ASKED to measure, versus frames the analyzer actually recorded. They differ when
    // the window is longer than the analyzer's 2000-frame ring buffer, or when a long event (an
    // autosave, a map regen) ate part of it. Both are recorded because every mean in this table is
    // divided by the second number, and a reader comparing two runs needs to know the divisor moved.
    public int RequestedFrames { get; set; }

    public int MeasuredFrames { get; set; }

    // Mean milliseconds per Root_Play.Update during the window, as the analyzer measures it. This is
    // the denominator of PercentOfFrame, and it is recorded rather than left implicit because the bare
    // percentage is a trap: the analyzer reports a share of the CURRENT frame, so on a machine running
    // the harness at ~350 fps a "15.9% of frame" row is 15.9% of 2.85 ms — about 2.7% of a 60 fps
    // budget. Without the denominator on the page, that row reads roughly six times more alarming than
    // it is. PercentOfSixtyFpsBudget on each row is the same number expressed against the budget
    // people actually mean.
    public double FrameMs { get; set; }

    // 1000 / FrameMs, so nobody reading the report has to do it. Zero when FrameMs is zero.
    public double FramesPerSecond { get; set; }

    // How many of this window's frames the game was PAUSED for, out of how many the driver sampled.
    // Recorded because run-level profiling deliberately does not force a game speed — it must not
    // change what the scenarios it wraps around do — so a scenario that jumped the clock leaves the
    // colony paused and every tick-driven patch in this table reads as free through no merit of its
    // own. See RunProfiling.PausedNote, which ProfileMath copies into Notes whenever this is non-zero.
    // Zero/zero on a table from an explicit Profile step, which forces a speed instead.
    public int PausedFrames { get; set; }

    public int SampledFrames { get; set; }

    // Every metric summed/maxed over ALL matched rows, as a single pseudo-row labelled "*". This is
    // what a ProfileAssert with label="*" reads, and it is stored rather than derived from Rows for one
    // load-bearing reason: Rows is capped (RunProfiling.MaxRows) and totals derived from a truncated
    // list would quietly describe only the rows that survived the cap. A subsystem's real cost silently
    // shrinking because the report got long is exactly the plausible-wrong-number failure this file
    // exists to prevent.
    //
    // Per-metric rather than a blanket sum: see ProfileMath.Totals for why summing a maximum would be
    // nonsense.
    public ProfileRow Totals { get; set; } = new();

    // How many profiled methods the analyzer had before `Prefix` was applied. A filter that matched
    // nothing is an error (see ProfileMath), and this is what makes "matched 3 of 812" legible.
    public int RowsBeforeFilter { get; set; }

    // How many matched the prefix, before the row cap. Equal to Rows.Count unless the table was
    // truncated; the gap is what TruncationNote reports.
    public int RowsMatched { get; set; }

    // Sorted by AvgMsPerFrame descending, ties broken by Label, so two runs of the same scenario
    // produce diffable JSON rather than whatever order a ConcurrentDictionary enumerated in. Capped at
    // RunProfiling.MaxRows — see Totals for what the cap does NOT affect.
    public List<ProfileRow> Rows { get; set; } = new();

    // Caveats that belong WITH the numbers rather than in documentation nobody re-reads while looking
    // at a report. Populated by ProfileMath; see ProfileMath.Notes for the text and the reasoning.
    public List<string> Notes { get; set; } = new();
}
