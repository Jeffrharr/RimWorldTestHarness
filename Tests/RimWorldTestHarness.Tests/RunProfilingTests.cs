using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Tests;

// Covers the decisions run-level profiling makes, all of which come down to one question: when is a
// number worth writing down?
//
// Every branch tested here exists because the alternative is a table of zeroes, and zeroes are the
// worst possible output — they survive review, because they look exactly like a measurement of
// something cheap. Getting these wrong would mean a green run reporting "this mod is free" over a run
// in which nothing was measured at all, which is precisely the failure this repo exists to prevent.
[TestFixture]
public class RunProfilingSkipReasonTests
{
    [Test]
    public void ProfilingNotAskedForIsNotASkip()
    {
        Assert.That(RunProfiling.BeforeStartSkipReason(requested: false, null, analyzerLoaded: false),
                    Is.Null);
    }

    // The runner scanned the disk; we did not. Its verdict is the one with the actionable detail in
    // it ("subscribe to 2038874626", "drop a copy in <path>"), so it wins over anything derived from
    // inside the game.
    [Test]
    public void TheRunnersOwnReasonWinsOverTheInGameOne()
    {
        string? reason = RunProfiling.BeforeStartSkipReason(
            requested: true, "not installed on this machine", analyzerLoaded: false);

        Assert.That(reason, Is.EqualTo("not installed on this machine"));
    }

    // The runner put it in ModsConfig and RimWorld did not load it — a different problem from "not
    // installed", and one the runner cannot see.
    [Test]
    public void AskedForButAbsentFromTheLoadReportsTheNotLoadedReason()
    {
        Assert.That(RunProfiling.BeforeStartSkipReason(requested: true, null, analyzerLoaded: false),
                    Is.EqualTo(RunProfiling.NotLoadedReason));
    }

    [Test]
    public void AskedForAndLoadedProceeds()
    {
        Assert.That(RunProfiling.BeforeStartSkipReason(requested: true, "", analyzerLoaded: true),
                    Is.Null);
    }

    private static string? After(
        bool gameReached = true, bool windowOpened = true, string? startError = null,
        int measuredFrames = 600, int methodsThatRan = 3) =>
        RunProfiling.AfterWindowSkipReason(
            gameReached, windowOpened, startError, measuredFrames, methodsThatRan);

    // THE case requirement three is about: a run that verifies XML patch behaviour and never reaches a
    // map. It is a normal way to use the harness, not an edge case, and it must say so rather than
    // report a table of zeroes.
    [Test]
    public void ARunThatNeverBecameInteractiveSaysSo()
    {
        // Deliberately checked with a window that "opened" and frames that "elapsed": the no-game case
        // has to win over every symptom downstream of it, because it is the only one that names a cause.
        Assert.That(After(gameReached: false), Does.Contain("never became interactive"));
    }

    [Test]
    public void AFailedStartIsReportedVerbatim()
    {
        Assert.That(After(startError: "its API has changed"), Does.Contain("its API has changed"));
    }

    // A scenario with no steps never opens a window. Harvesting anyway would report the PREVIOUS
    // scenario's frames under this scenario's name, which is worse than reporting nothing.
    [Test]
    public void AScenarioThatRanNoStepsHasNoWindowToHarvest()
    {
        Assert.That(After(windowOpened: false), Does.Contain("ran no steps"));
    }

    [Test]
    public void NoFramesRecordedIsNamedRatherThanDividedBy()
    {
        Assert.That(After(measuredFrames: 0), Does.Contain("recorded no frames"));
    }

    // The paused-colony failure in another costume: a window of three frames produces per-frame means
    // that are one frame's noise wearing an average's clothes.
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(RunProfiling.MinimumFrames - 1)]
    public void AWindowTooShortToMeanAnythingIsRefused(int frames)
    {
        string? reason = After(measuredFrames: frames);

        Assert.Multiple(() =>
        {
            Assert.That(reason, Does.Contain($"{RunProfiling.MinimumFrames}"));
            // The actual count is in the message: "too short" without a number leaves the reader
            // unable to tell a 29-frame window from a 1-frame one.
            Assert.That(reason, Does.Contain(frames == 1 ? "1 frame" : frames.ToString()));
        });
    }

    [Test]
    public void ExactlyTheMinimumIsAccepted()
    {
        Assert.That(After(measuredFrames: RunProfiling.MinimumFrames), Is.Null);
    }

    [Test]
    public void NothingInstrumentedRunningIsNamedRatherThanTabulatedAsZero()
    {
        Assert.That(After(methodsThatRan: 0), Does.Contain("no instrumented method ran"));
    }

    [Test]
    public void AnOrdinaryWindowProducesNoReason()
    {
        Assert.That(After(), Is.Null);
    }
}

// The guardrail. These tests are the specification of what a `pinnedUnder` declaration buys, which is
// the difference between a marker somebody has to notice and a run that fails.
[TestFixture]
public class ProbePinningTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase(ProbePinning.Any)]
    [TestCase(ProbePinning.Profiler)]
    [TestCase(ProbePinning.NoProfiler)]
    public void AcceptsAbsentAndEveryKnownValue(string? raw)
    {
        Assert.That(ProbePinning.Validate(raw, out string? error), Is.True, error);
    }

    // A misspelling must fail at LOAD, because it would otherwise parse as "any" and silently gate
    // nothing — leaving the author believing they had the protection they wrote down.
    [TestCase("no-profier")]
    [TestCase("noprofiler")]
    [TestCase("true")]
    [TestCase("NO-PROFILER")]
    public void RejectsAnythingElse(string raw)
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProbePinning.Validate(raw, out string? error), Is.False);
            Assert.That(ProbePinning.Validate(raw, out string? e2) ? null : e2, Does.Contain(raw));
        });
    }

    [TestCase(null, true)]
    [TestCase(null, false)]
    [TestCase("", true)]
    [TestCase(ProbePinning.Any, true)]
    [TestCase(ProbePinning.Any, false)]
    public void AnUndeclaredProbeNeverBlocks(string? pinnedUnder, bool runProfiled)
    {
        Assert.That(ProbePinning.Mismatch(pinnedUnder, runProfiled, "p"), Is.Null);
    }

    [TestCase(ProbePinning.Profiler, true)]
    [TestCase(ProbePinning.NoProfiler, false)]
    public void MatchingModesPass(string pinnedUnder, bool runProfiled)
    {
        Assert.That(ProbePinning.Mismatch(pinnedUnder, runProfiled, "p"), Is.Null);
    }

    // The default-on direction, and the one this exists for: a value pinned before profiling became
    // the default, re-checked by an ordinary run today.
    [Test]
    public void AValuePinnedWithoutTheProfilerFailsInAProfiledRun()
    {
        string? mismatch = ProbePinning.Mismatch(ProbePinning.NoProfiler, runProfiled: true, "aurora_cost");

        Assert.Multiple(() =>
        {
            Assert.That(mismatch, Does.Contain("aurora_cost"));
            // The fix travels with the complaint. A guardrail that says "these are incomparable" and
            // stops there just moves the puzzle.
            Assert.That(mismatch, Does.Contain("--no-profiler"));
        });
    }

    [Test]
    public void AValuePinnedUnderTheProfilerFailsInAnUnprofiledRun()
    {
        string? mismatch = ProbePinning.Mismatch(ProbePinning.Profiler, runProfiled: false, "aurora_cost");

        Assert.Multiple(() =>
        {
            Assert.That(mismatch, Does.Contain("aurora_cost"));
            Assert.That(mismatch, Does.Contain("not profiled"));
        });
    }

    // The step spec is where a typo has to be caught; the driver only ever sees values that got past it.
    [Test]
    public void TheProbeStepSpecRefusesAMisspelledDeclaration()
    {
        var step = new ProbeStep();
        var args = new Dictionary<string, string> { { StepArgs.ProbePinnedUnder, "no-profier" } };

        Assert.That(step.TryValidate(args, out string? error), Is.False, error);
    }

    [Test]
    public void TheProbeStepSpecAcceptsAProbeThatSaysNothing()
    {
        var step = new ProbeStep();
        var args = new Dictionary<string, string> { { StepArgs.ProbeName, "ticks_abs" } };

        Assert.That(step.TryValidate(args, out string? error), Is.True, error);
    }
}

// Run-level tables have no prefix filter and are therefore capped. These cover the one thing a cap can
// silently break.
[TestFixture]
public class ProfileTableCapTests
{
    private static ProfileSample Sample(string label, double avgMs, double calls) =>
        new ProfileSample
        {
            Label = label,
            AverageMs = avgMs,
            MaxMs = avgMs * 2,
            TotalMs = avgMs * 100,
            Calls = calls,
            MaxCallsPerFrame = calls,
        };

    private static ProfileTable Build(int maxRows, int sampled = 0, int paused = 0)
    {
        var samples = new List<ProfileSample>();
        for (int i = 0; i < 10; i++)
            samples.Add(Sample($"Mod.P{i}:M", 0.1 * (i + 1), 100));

        return ProfileMath.Build("t", "harmony", "", 100, 100, 2.0, samples, maxRows, sampled, paused);
    }

    // The whole reason ProfileTable.Totals is stored rather than derived: a truncated table whose
    // totals described only the surviving rows would report a subsystem getting cheaper because the
    // report got long.
    [Test]
    public void TotalsCoverEveryMatchedRowNotOnlyTheOnesThatSurvivedTheCap()
    {
        ProfileTable capped = Build(maxRows: 3);
        ProfileTable whole = Build(maxRows: int.MaxValue);

        Assert.Multiple(() =>
        {
            Assert.That(capped.Rows, Has.Count.EqualTo(3));
            Assert.That(capped.RowsMatched, Is.EqualTo(10));
            Assert.That(capped.Totals.AvgMsPerFrame, Is.EqualTo(whole.Totals.AvgMsPerFrame));
            Assert.That(capped.Totals.Calls, Is.EqualTo(whole.Totals.Calls));
            Assert.That(capped.Totals.MaxCallsPerFrame, Is.EqualTo(whole.Totals.MaxCallsPerFrame));
        });
    }

    // label="*" reads the stored row, so it agrees with the totals rather than with the visible rows.
    [Test]
    public void AssertingOnTheTotalsIgnoresTheCap()
    {
        ProfileTable capped = Build(maxRows: 3);

        Assert.That(
            ProfileMetrics.TryRead(capped, ProfileMetrics.TotalsLabel, ProfileMetrics.Calls,
                                   out double calls, out string? error),
            Is.True, error);
        Assert.That(calls, Is.EqualTo(1000));
    }

    // A row the cap removed and a row that never ran produce the same "not found", and they need very
    // different fixes.
    [Test]
    public void ARowLostToTheCapSaysSoRatherThanReadingAsAbsent()
    {
        ProfileTable capped = Build(maxRows: 3);

        ProfileMetrics.TryRead(capped, "Mod.P0:M", ProfileMetrics.Calls, out _, out string? error);

        Assert.That(error, Does.Contain("most expensive"));
    }

    [Test]
    public void TruncationIsRecordedInTheTablesOwnNotes()
    {
        Assert.That(Build(maxRows: 3).Notes,
                    Has.Some.Contains("Showing the 3 most expensive of 10 rows"));
    }

    [Test]
    public void AnUncappedTableSaysNothingAboutTruncation()
    {
        Assert.That(Build(maxRows: int.MaxValue).Notes,
                    Has.None.Contains("most expensive"));
    }

    // A paused window measures render cost only. The note is the disclosure that stops someone reading
    // an absent tick-driven row as a cheap one.
    [Test]
    public void PausedFramesAreDisclosedInTheNotes()
    {
        ProfileTable table = Build(maxRows: int.MaxValue, sampled: 100, paused: 100);

        Assert.Multiple(() =>
        {
            Assert.That(table.PausedFrames, Is.EqualTo(100));
            Assert.That(table.Notes, Has.Some.Contains("PAUSED for 100 of 100 frames"));
        });
    }

    [Test]
    public void AnUnpausedWindowSaysNothingAboutPausing()
    {
        Assert.That(Build(maxRows: int.MaxValue, sampled: 100, paused: 0).Notes,
                    Has.None.Contains("PAUSED"));
    }
}
