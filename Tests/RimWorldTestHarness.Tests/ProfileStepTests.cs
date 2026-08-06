using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;

namespace RimWorldTestHarness.Tests;

// Covers the desugaring of a Profile step and every way its args can be rejected, for the same reason
// TickLapseExpanderTests exists: all of it is verifiable with no game and no profiler installed, and a
// composite that silently expands into the wrong window is a run that measures something other than
// what it says.
[TestFixture]
public class ProfileExpanderTests
{
    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> args = new Dictionary<string, string>();
        foreach ((string key, string value) in pairs)
            args[key] = value;
        return args;
    }

    private static Dictionary<string, string> Valid(params (string Key, string Value)[] extra)
    {
        Dictionary<string, string> args = Args(
            (StepArgs.ProfileName, "aurora"),
            (StepArgs.ProfilePrefix, "CelestialLighting"));
        foreach ((string key, string value) in extra)
            args[key] = value;
        return args;
    }

    [Test]
    public void ExpandsIntoStartMeasureStop()
    {
        Assert.That(ProfileExpander.TryExpand(Valid(), out List<ScenarioStep> steps, out _), Is.True);

        Assert.That(steps.Select(s => s.Type), Is.EqualTo(new[]
        {
            StepArgs.ProfileStartType, StepArgs.ProfileMeasureType, StepArgs.ProfileStopType,
        }));
    }

    // The measured window is on Measure, not on Start: Start's own frames are warmup, and folding them
    // together would attribute each transplanted method's one-off JIT cost to whichever patch ran first.
    [Test]
    public void PutsTheWindowOnMeasureAndTheWarmupOnStart()
    {
        ProfileExpander.TryExpand(
            Valid((StepArgs.ProfileFrames, "300"), (StepArgs.ProfileWarmupFrames, "10")),
            out List<ScenarioStep> steps, out _);

        Assert.Multiple(() =>
        {
            Assert.That(steps[0].Args[StepArgs.ProfileWarmupFrames], Is.EqualTo("10"));
            Assert.That(steps[1].Args[StepArgs.ProfileFrames], Is.EqualTo("300"));
            Assert.That(steps[2].Args[StepArgs.ProfileName], Is.EqualTo("aurora"));
            Assert.That(steps[2].Args[StepArgs.ProfilePrefix], Is.EqualTo("CelestialLighting"));
        });
    }

    [Test]
    public void DefaultsAreAWatchableWindowAtNormalSpeed()
    {
        ProfileExpander.TryExpand(Valid(), out List<ScenarioStep> steps, out _);

        Assert.Multiple(() =>
        {
            Assert.That(steps[0].Args[StepArgs.ProfileTimeSpeed], Is.EqualTo("normal"));
            Assert.That(steps[0].Args[StepArgs.ProfileEntry], Is.EqualTo("harmony"));
            Assert.That(steps[1].Args[StepArgs.ProfileFrames],
                        Is.EqualTo(ProfileExpander.DefaultFrames.ToString()));
        });
    }

    // Required rather than defaulted to "": an unnamed table cannot be asserted on, and an unfiltered
    // one is several thousand rows of vanilla in the report.
    [TestCase(StepArgs.ProfileName)]
    [TestCase(StepArgs.ProfilePrefix)]
    public void RefusesToExpandWithoutNameOrPrefix(string missing)
    {
        Dictionary<string, string> args = Valid();
        args.Remove(missing);

        Assert.Multiple(() =>
        {
            Assert.That(ProfileExpander.TryExpand(args, out _, out string? error), Is.False);
            Assert.That(error, Does.Contain(missing));
        });
    }

    // Refused rather than clamped: past the analyzer's ring buffer the window silently becomes "the
    // last 1999 frames", so the run would report means over a fraction of the span it thinks it took.
    [Test]
    public void RefusesAWindowLongerThanTheAnalyzersRingBuffer()
    {
        bool ok = ProfileExpander.TryExpand(
            Valid((StepArgs.ProfileFrames, "5000")), out _, out string? error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain(ProfileMath.MaxFrames.ToString()));
        });
    }

    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("six hundred")]
    public void RejectsANonPositiveOrUnparsableFrameCount(string frames)
    {
        Assert.That(ProfileExpander.TryExpand(
            Valid((StepArgs.ProfileFrames, frames)), out _, out _), Is.False);
    }

    // A typo'd speed must fail at load, not silently leave the game paused — profiling a paused colony
    // measures a load of tick-driven patches that never fire and reports the mod as free.
    [Test]
    public void RejectsAnUnknownTimeSpeed()
    {
        bool ok = ProfileExpander.TryExpand(
            Valid((StepArgs.ProfileTimeSpeed, "nromal")), out _, out string? error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("nromal"));
        });
    }

    [Test]
    public void RejectsAnUnknownArgRatherThanIgnoringIt()
    {
        Assert.That(ProfileExpander.TryExpand(
            Valid(("framse", "600")), out _, out _), Is.False);
    }
}

// Covers the bound grammar, which is the part of ProfileAssert that decides whether a run is gated at
// all.
[TestFixture]
public class ProfileAssertArgsTests
{
    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> args = new Dictionary<string, string>();
        foreach ((string key, string value) in pairs)
            args[key] = value;
        return args;
    }

    private static Dictionary<string, string> Base(params (string Key, string Value)[] extra)
    {
        Dictionary<string, string> args = Args(
            (StepArgs.ProfileAssertTable, "aurora"),
            (StepArgs.ProfileAssertMetric, ProfileMetrics.CallsPerFrame));
        foreach ((string key, string value) in extra)
            args[key] = value;
        return args;
    }

    [Test]
    public void MaxBecomesAnAtMostBound()
    {
        ProfileAssertArgs.TryParse(Base((StepArgs.ProfileAssertMax, "2.5")),
                                   out ProfileAssertArgs.Parsed parsed, out _);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Bound.Comparison, Is.EqualTo(ProbeComparison.AtMost));
            Assert.That(parsed.Bound.IsSatisfiedBy(2.5), Is.True);
            Assert.That(parsed.Bound.IsSatisfiedBy(2.51), Is.False);
        });
    }

    [Test]
    public void MinBecomesAnAtLeastBound()
    {
        ProfileAssertArgs.TryParse(Base((StepArgs.ProfileAssertMin, "1")),
                                   out ProfileAssertArgs.Parsed parsed, out _);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Bound.Comparison, Is.EqualTo(ProbeComparison.AtLeast));
            Assert.That(parsed.Bound.IsSatisfiedBy(1), Is.True);
            Assert.That(parsed.Bound.IsSatisfiedBy(0.99), Is.False);
        });
    }

    [Test]
    public void ExpectedPlusToleranceIsTheTwoSidedProbeShape()
    {
        ProfileAssertArgs.TryParse(
            Base((StepArgs.ProfileAssertExpectedValue, "6"), (StepArgs.ProfileAssertTolerance, "0.5")),
            out ProfileAssertArgs.Parsed parsed, out _);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Bound.Comparison, Is.EqualTo(ProbeComparison.Within));
            Assert.That(parsed.Bound.IsSatisfiedBy(6.4), Is.True);
            Assert.That(parsed.Bound.IsSatisfiedBy(5.4), Is.False);
        });
    }

    // Two bounds is someone expecting a range check. Honouring the first silently would pass a run that
    // never checked the other end.
    [Test]
    public void TwoBoundFormsInOneStepIsAnError()
    {
        bool ok = ProfileAssertArgs.TryParse(
            Base((StepArgs.ProfileAssertMax, "2"), (StepArgs.ProfileAssertMin, "1")), out _, out string? error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("exactly one"));
        });
    }

    [Test]
    public void NoBoundAtAllIsAnError()
    {
        Assert.That(ProfileAssertArgs.TryParse(Base(), out _, out _), Is.False);
    }

    // An exact float match on a measured number never passes, so a missing tolerance has to be a load
    // error rather than a run that is always red.
    [Test]
    public void ExpectedWithoutToleranceIsAnError()
    {
        bool ok = ProfileAssertArgs.TryParse(
            Base((StepArgs.ProfileAssertExpectedValue, "6")), out _, out string? error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain(StepArgs.ProfileAssertTolerance));
        });
    }

    // A tolerance next to a one-sided bound reads as widening it. It does not, and ignoring the key the
    // author wrote would leave them believing in slack they never got.
    [Test]
    public void ToleranceAlongsideMaxIsAnError()
    {
        Assert.That(ProfileAssertArgs.TryParse(
            Base((StepArgs.ProfileAssertMax, "2"), (StepArgs.ProfileAssertTolerance, "1")),
            out _, out _), Is.False);
    }

    [Test]
    public void LabelDefaultsToTheWholeTable()
    {
        ProfileAssertArgs.TryParse(Base((StepArgs.ProfileAssertMax, "2")),
                                   out ProfileAssertArgs.Parsed parsed, out _);

        Assert.That(parsed.Label, Is.EqualTo(ProfileMetrics.TotalsLabel));
    }

    [Test]
    public void CheckNameIsStableAndDiffable()
    {
        Assert.That(ProfileAssertArgs.CheckName("aurora", "*", "avgMsPerFrame"),
                    Is.EqualTo("profile:aurora/*.avgMsPerFrame"));
    }
}

// The properties a profiling step gets wrong most dangerously: whether it is reachable from a real
// player's colony, and whether it forces isolation afterwards.
[TestFixture]
public class ProfileStepRegistrationTests
{
    [TestCase(StepArgs.ProfileType)]
    [TestCase(StepArgs.ProfileStartType)]
    [TestCase(StepArgs.ProfileMeasureType)]
    [TestCase(StepArgs.ProfileStopType)]
    [TestCase(StepArgs.ProfileAssertType)]
    public void EveryProfilingStepIsDiscoveredAndNotLiveCallable(string type)
    {
        Assert.That(StepRegistry.TryGet(type, out IStepSpec? spec), Is.True);
        Assert.That(spec!.LiveCallable, Is.False,
                    "profiling rewrites the IL of every patched method in the load — never run it " +
                    "against someone's colony");
    }

    // The residue with the least visible symptom and the worst consequence: the world looks untouched
    // while every subsequent timing measurement in the load is reading transplanted method bodies.
    [TestCase(StepArgs.ProfileType)]
    [TestCase(StepArgs.ProfileStartType)]
    [TestCase(StepArgs.ProfileMeasureType)]
    [TestCase(StepArgs.ProfileStopType)]
    public void ProfilingResidueForcesAReload(string type)
    {
        ScenarioResidue residue = ScenarioResidueAnalyzer.OfStep(type);

        Assert.Multiple(() =>
        {
            Assert.That(residue.HasFlag(ScenarioResidue.Profiler), Is.True);
            Assert.That(residue & ScenarioResidueAnalyzer.RequiresReload, Is.Not.EqualTo(ScenarioResidue.None));
        });
    }

    // A malformed Profile is left in place rather than dropped, so it must not look cheaper to isolate
    // than the valid one it failed to become.
    [Test]
    public void AnUnexpandedProfileStillDeclaresItsExpansionsResidue()
    {
        Assert.That(ScenarioResidueAnalyzer.OfStep(StepArgs.ProfileType),
                    Is.EqualTo(ScenarioResidueAnalyzer.OfStep(StepArgs.ProfileStartType)));
    }

    [Test]
    public void ProfileAssertLeavesNothingBehind()
    {
        Assert.That(ScenarioResidueAnalyzer.OfStep(StepArgs.ProfileAssertType),
                    Is.EqualTo(ScenarioResidue.None));
    }
}
