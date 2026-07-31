using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;

namespace RimWorldTestHarness.Tests;

// Edge-case coverage for the tick sweep's desugaring, for the same reason TimelapseExpanderTests
// exists: the frame maths and every rejection path are verifiable with no game running.
[TestFixture]
public class TickLapseExpanderTests
{
    private static ScenarioStep TickLapse(params (string Key, string Value)[] args)
    {
        ScenarioStep step = new ScenarioStep { Type = StepArgs.TickLapseType };
        foreach ((string key, string value) in args)
            step.Args[key] = value;
        return step;
    }

    private static List<ScenarioStep> Expand(ScenarioStep step, out List<string> errors)
    {
        errors = new List<string>();
        return StepExpansion.ExpandAll(new[] { step }, errors);
    }

    private static List<string> Advances(List<ScenarioStep> steps) =>
        steps.Where(s => s.Type == StepArgs.AdvanceTicksType)
             .Select(s => s.Args[StepArgs.AdvanceTicksTicks])
             .ToList();

    private static List<string> FrameNames(List<ScenarioStep> steps) =>
        steps.Where(s => s.Type == StepArgs.ScreenshotType)
             .Select(s => s.Args[StepArgs.ScreenshotFileName])
             .ToList();

    // --- happy path ---

    [Test]
    public void Expand_Defaults_ProduceASixSecondClipAtTwentyFps()
    {
        var steps = Expand(TickLapse(), out var errors);

        Assert.That(errors, Is.Empty);
        // 120 frames x (AdvanceTicks + Wait + Screenshot).
        Assert.That(steps, Has.Count.EqualTo(360));
        Assert.That(FrameNames(steps), Has.Count.EqualTo(TickLapseExpander.DefaultSteps));
    }

    // The whole reason this step exists: the interval between two captures is exact and identical,
    // so the frames are evenly spaced in game time and the clip does not judder.
    [Test]
    public void Expand_EveryFrameAdvancesTheSameNumberOfTicks()
    {
        var steps = Expand(TickLapse(("ticks", "12"), ("steps", "4")), out _);

        Assert.That(Advances(steps), Is.EqualTo(new[] { "12", "12", "12", "12" }));
    }

    // No absolute-versus-relative special case for frame 0, unlike Timelapse: every frame is the
    // same triple, in the same order.
    [Test]
    public void Expand_EveryFrameHasTheSameShape()
    {
        var steps = Expand(TickLapse(("steps", "2")), out _);

        Assert.That(steps.Select(s => s.Type), Is.EqualTo(new[]
        {
            StepArgs.AdvanceTicksType, StepArgs.WaitType, StepArgs.ScreenshotType,
            StepArgs.AdvanceTicksType, StepArgs.WaitType, StepArgs.ScreenshotType,
        }));
    }

    // Shared with Timelapse so the runner has one stitching path: zero-padded to four digits, which
    // is what ffmpeg's %04d pattern reads.
    [Test]
    public void Expand_FrameNames_AreZeroPaddedAndPrefixed()
    {
        var steps = Expand(TickLapse(("steps", "3"), ("fileNamePrefix", "aurora")), out _);

        Assert.That(FrameNames(steps), Is.EqualTo(new[]
        {
            "aurora_0000.png", "aurora_0001.png", "aurora_0002.png",
        }));
    }

    [Test]
    public void Expand_ZeroSettleFrames_OmitsTheWaitEntirely()
    {
        var steps = Expand(TickLapse(("steps", "2"), ("settleFrames", "0")), out var errors);

        Assert.That(errors, Is.Empty);
        Assert.That(steps.Any(s => s.Type == StepArgs.WaitType), Is.False);
        Assert.That(steps, Has.Count.EqualTo(4));
    }

    // --- rejections ---

    // A composite whose args don't validate is left in place rather than dropped, so a scenario that
    // lost a whole clip to a typo fails loudly instead of running short and reporting green.
    private static void AssertRejected(ScenarioStep step, string expectedFragment)
    {
        var steps = Expand(step, out var errors);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain(expectedFragment));
        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Type, Is.EqualTo(StepArgs.TickLapseType));
    }

    [Test]
    public void Expand_ZeroTicks_IsRejected() =>
        // Would capture the same instant `steps` times: a still, reported as a clip.
        AssertRejected(TickLapse(("ticks", "0")), StepArgs.TickLapseTicks);

    [Test]
    public void Expand_NegativeTicks_IsRejected() =>
        AssertRejected(TickLapse(("ticks", "-5")), StepArgs.TickLapseTicks);

    [Test]
    public void Expand_ZeroSteps_IsRejected() =>
        AssertRejected(TickLapse(("steps", "0")), StepArgs.TickLapseSteps);

    [Test]
    public void Expand_MoreFramesThanTheCap_IsRejected() =>
        AssertRejected(
            TickLapse(("steps", (TickLapseExpander.MaxFrames + 1).ToString())),
            $"{TickLapseExpander.MaxFrames}-frame cap");

    [Test]
    public void Expand_ExactlyTheCap_IsAccepted()
    {
        Expand(TickLapse(("steps", TickLapseExpander.MaxFrames.ToString())), out var errors);

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Expand_NegativeSettleFrames_IsRejected() =>
        AssertRejected(TickLapse(("settleFrames", "-1")), StepArgs.TickLapseSettleFrames);

    [Test]
    public void Expand_FpsOutOfRange_IsRejected() =>
        // Validated here even though only the runner consumes it, so a bad value fails before the
        // run rather than after every frame has already been captured.
        AssertRejected(TickLapse(("fps", "0")), StepArgs.TickLapseFps);

    [Test]
    public void Expand_EmptyPrefix_IsRejected() =>
        AssertRejected(TickLapse(("fileNamePrefix", "  ")), StepArgs.TickLapseFileNamePrefix);

    // Timelapse's key names are close enough to this step's that a mistyped one would otherwise be
    // silently ignored and the clip would come out with the defaults it wasn't asked for.
    [Test]
    public void Expand_UnknownArg_IsRejected() =>
        AssertRejected(TickLapse(("stepHours", "1")), "stepHours");
}
