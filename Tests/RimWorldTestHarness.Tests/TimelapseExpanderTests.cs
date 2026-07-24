using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// Edge-case coverage for the desugaring itself. This is the whole reason Timelapse expands in
// Shared/ rather than inside the driver: the frame maths, the half-open range, and every rejection
// path are verifiable offline with no game running.
[TestFixture]
public class TimelapseExpanderTests
{
    private static ScenarioStep Timelapse(params (string Key, string Value)[] args)
    {
        ScenarioStep step = new ScenarioStep { Type = StepArgs.TimelapseType };
        foreach ((string key, string value) in args)
            step.Args[key] = value;
        return step;
    }

    private static List<ScenarioStep> Expand(ScenarioStep step, out List<string> errors)
    {
        errors = new List<string>();
        return TimelapseExpander.ExpandAll(new[] { step }, errors);
    }

    // --- happy path ---

    [Test]
    public void Expand_FullDayHourly_ProducesOneTriplePerHour()
    {
        var steps = Expand(Timelapse(), out var errors);

        Assert.That(errors, Is.Empty);
        // 24 frames x (SetTime + Wait + Screenshot); half-open, so hour 24 is not a 25th frame.
        Assert.That(steps, Has.Count.EqualTo(72));
        Assert.That(steps[0].Type, Is.EqualTo(StepArgs.SetTimeType));
        Assert.That(steps[1].Type, Is.EqualTo(StepArgs.WaitType));
        Assert.That(steps[2].Type, Is.EqualTo(StepArgs.ScreenshotType));
    }

    [Test]
    public void Expand_HalfOpenRange_ExcludesEndpoint()
    {
        var steps = Expand(Timelapse(("fromHour", "6"), ("toHour", "18"), ("stepHours", "6")), out _);

        var hours = steps.Where(s => s.Type == StepArgs.SetTimeType)
                         .Select(s => s.Args[StepArgs.SetTimeHour])
                         .ToList();
        Assert.That(hours, Is.EqualTo(new[] { "6", "12" }));
    }

    [Test]
    public void Expand_FractionalStep_ComputesHoursFromIndexWithoutDrift()
    {
        var steps = Expand(Timelapse(("fromHour", "0"), ("toHour", "1"), ("stepHours", "0.25")), out _);

        var hours = steps.Where(s => s.Type == StepArgs.SetTimeType)
                         .Select(s => s.Args[StepArgs.SetTimeHour])
                         .ToList();
        Assert.That(hours, Is.EqualTo(new[] { "0", "0.25", "0.5", "0.75" }));
    }

    [Test]
    public void Expand_FrameFileNames_AreZeroPaddedAndSequential()
    {
        var steps = Expand(Timelapse(("toHour", "3"), ("fileNamePrefix", "daycycle")), out _);

        var names = steps.Where(s => s.Type == StepArgs.ScreenshotType)
                         .Select(s => s.Args[StepArgs.ScreenshotFileName])
                         .ToList();
        Assert.That(names, Is.EqualTo(new[] { "daycycle_0000.png", "daycycle_0001.png", "daycycle_0002.png" }));
    }

    [Test]
    public void Expand_SettleFramesZero_OmitsWaitSteps()
    {
        var steps = Expand(Timelapse(("toHour", "2"), ("settleFrames", "0")), out _);

        Assert.That(steps.Any(s => s.Type == StepArgs.WaitType), Is.False);
        Assert.That(steps, Has.Count.EqualTo(4));
    }

    [Test]
    public void Expand_SettleFrames_CarriedOntoWaitStep()
    {
        var steps = Expand(Timelapse(("toHour", "1"), ("settleFrames", "7")), out _);

        var wait = steps.Single(s => s.Type == StepArgs.WaitType);
        Assert.That(wait.Args[StepArgs.WaitFrames], Is.EqualTo("7"));
    }

    [Test]
    public void ExpandAll_LeavesOtherStepTypesUntouched()
    {
        ScenarioStep probe = new ScenarioStep { Type = StepArgs.ProbeType };
        probe.Args[StepArgs.ProbeName] = "shadow_lean";
        var errors = new List<string>();

        var steps = TimelapseExpander.ExpandAll(new[] { probe }, errors);

        Assert.That(errors, Is.Empty);
        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0], Is.SameAs(probe));
    }

    // --- rejection paths: each leaves the step in place AND records why ---

    [TestCase("stepHours", "0", "greater than 0")]
    [TestCase("stepHours", "-1", "greater than 0")]
    [TestCase("fromHour", "-1", "0..24")]
    [TestCase("toHour", "25", "0..24")]
    [TestCase("settleFrames", "-1", "must not be negative")]
    [TestCase("fps", "0", "between 1 and 60")]
    [TestCase("fps", "61", "between 1 and 60")]
    [TestCase("fileNamePrefix", "", "must not be empty")]
    [TestCase("stepHours", "abc", "is not a number")]
    [TestCase("settleFrames", "1.5", "is not a whole number")]
    public void Expand_InvalidArg_RecordsErrorAndLeavesStepUnexpanded(string key, string value, string expectedFragment)
    {
        var steps = Expand(Timelapse((key, value)), out var errors);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain(expectedFragment));
        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Type, Is.EqualTo(StepArgs.TimelapseType));
    }

    [Test]
    public void Expand_MidnightCrossingRange_IsRejectedWithGuidance()
    {
        var steps = Expand(Timelapse(("fromHour", "18"), ("toHour", "6")), out var errors);

        Assert.That(errors[0], Does.Contain("use two Timelapse steps"));
        Assert.That(steps[0].Type, Is.EqualTo(StepArgs.TimelapseType));
    }

    // A typo'd key must not silently fall back to a default — that would produce a plausible but
    // wrong video, the worst outcome for a tool whose job is verification.
    [Test]
    public void Expand_UnknownArgKey_IsRejectedRatherThanIgnored()
    {
        var steps = Expand(Timelapse(("stephours", "2")), out var errors);

        Assert.That(errors[0], Does.Contain("unknown arg 'stephours'"));
        Assert.That(steps, Has.Count.EqualTo(1));
    }

    [Test]
    public void Expand_ExceedingFrameCap_IsRejected()
    {
        var steps = Expand(Timelapse(("stepHours", "0.001")), out var errors);

        Assert.That(errors[0], Does.Contain($"{TimelapseExpander.MaxFrames}-frame cap"));
        Assert.That(steps, Has.Count.EqualTo(1));
    }

    [Test]
    public void Expand_ExactlyAtFrameCap_IsAccepted()
    {
        // 512 frames from a 512-step sweep across the full day — the boundary itself must pass.
        var steps = Expand(Timelapse(("stepHours", (24.0 / TimelapseExpander.MaxFrames).ToString("R")), ("settleFrames", "0")), out var errors);

        Assert.That(errors, Is.Empty);
        Assert.That(steps.Count(s => s.Type == StepArgs.ScreenshotType), Is.EqualTo(TimelapseExpander.MaxFrames));
    }

    // Binary floating point makes 24/1 computable as 23.999999...; an unrounded Ceiling would turn
    // that into a duplicate 25th frame with the same timestamp as the first.
    [Test]
    public void Expand_DivisionLandingJustUnderWhole_DoesNotAddDuplicateFrame()
    {
        var steps = Expand(Timelapse(("fromHour", "0"), ("toHour", "24"), ("stepHours", "0.1")), out var errors);

        Assert.That(errors, Is.Empty);
        Assert.That(steps.Count(s => s.Type == StepArgs.ScreenshotType), Is.EqualTo(240));
    }
}
