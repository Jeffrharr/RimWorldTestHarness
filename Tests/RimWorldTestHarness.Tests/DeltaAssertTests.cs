using System.Collections.Generic;
using NUnit.Framework;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Tests;

// The load-time half of the delta tier. The measurement and the judgement are Python (Runner/
// frame_delta.py, Runner/delta_gate.py, tested under Tests/runner) because they happen after the
// game has exited; what is left in C# is deciding whether a scenario's declaration is even coherent
// — and doing it before a run spends minutes producing frames nobody can assert on.
[TestFixture]
public class DeltaAssertStepTests
{
    private static List<string> Validate(params (string, string)[] args)
    {
        List<string> errors = new List<string>();
        ScenarioStep step = new ScenarioStep { Type = AssertStep.StepType };
        foreach ((string k, string v) in args)
            step.Args[k] = v;

        StepValidator.ValidateAll(new[] { step }, errors);
        return errors;
    }

    private static (string, string) Kind => (AssertStep.KindArg, AssertStep.DeltaKind);
    private static (string, string) Baseline => (AssertStep.BaselineArg, "off.png");
    private static (string, string) Target => (AssertStep.TargetArg, "on.png");
    private static (string, string) Floor => (AssertStep.MinDeltaEArg, "2");

    [Test]
    public void AcceptsAMinimalDeltaAssert()
    {
        Assert.That(Validate(Kind, Baseline, Target, Floor), Is.Empty);
    }

    [Test]
    public void RequiresBothFrames()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Validate(Kind, Target, Floor)[0], Does.Contain(AssertStep.BaselineArg));
            Assert.That(Validate(Kind, Baseline, Floor)[0], Does.Contain(AssertStep.TargetArg));
        });
    }

    // Always a perfect zero, which reads as "no effect" — far more likely a copy-paste than an
    // assertion anyone meant to write.
    [Test]
    public void RejectsAFrameComparedWithItself()
    {
        Assert.That(Validate(Kind, (AssertStep.BaselineArg, "a.png"), (AssertStep.TargetArg, "a.png"), Floor)[0],
            Does.Contain("cannot differ from itself"));
    }

    // THE gate on the gate. A delta assert with no direction and no bounds measures two frames and
    // accepts every possible answer: a step that looks like a check and is not, which is the precise
    // failure the whole Assert tier was added to prevent.
    [Test]
    public void RejectsADeltaAssertThatWouldAcceptAnyResult()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Validate(Kind, Baseline, Target)[0], Does.Contain("every possible answer"));
            Assert.That(
                Validate(Kind, Baseline, Target, (AssertStep.DirectionArg, AssertStep.AnyDirection))[0],
                Does.Contain("every possible answer"),
                "direction=any is the absence of a direction, not a direction");
        });
    }

    [TestCase("brighter")]
    [TestCase("darker")]
    [TestCase("warmer")]
    [TestCase("cooler")]
    [TestCase("purpler")]
    [TestCase("greener")]
    public void ADirectionAloneIsEnoughToAssertSomething(string direction)
    {
        Assert.That(Validate(Kind, Baseline, Target, (AssertStep.DirectionArg, direction)), Is.Empty);
    }

    [Test]
    public void RejectsAnUnknownDirection()
    {
        Assert.That(Validate(Kind, Baseline, Target, (AssertStep.DirectionArg, "sideways"))[0],
            Does.Contain("sideways"));
    }

    [TestCase("full")]
    [TestCase("0,0,640,360")]
    [TestCase(" 10 , 20 , 30 , 40 ")]
    public void AcceptsAWellFormedRegion(string region)
    {
        Assert.That(Validate(Kind, Baseline, Target, Floor, (AssertStep.RegionArg, region)), Is.Empty);
    }

    [TestCase("1,2,3")]
    [TestCase("1,2,3,4,5")]
    [TestCase("a,b,c,d")]
    [TestCase("0,0,0,10")]
    [TestCase("0,0,10,-4")]
    public void RejectsAMalformedRegion(string region)
    {
        Assert.That(Validate(Kind, Baseline, Target, Floor, (AssertStep.RegionArg, region))[0],
            Does.Contain(AssertStep.RegionArg));
    }

    // Whether the rect fits inside the frame cannot be known until something has decoded a PNG, so
    // that check belongs to Runner/frame_delta.py's parse_region. This one only catches the typo.
    [Test]
    public void AcceptsARegionThatMayStillProveTooBigForTheFrame()
    {
        Assert.That(Validate(Kind, Baseline, Target, Floor, (AssertStep.RegionArg, "0,0,99999,99999")),
            Is.Empty);
    }

    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("half")]
    public void RejectsAStrideBelowOne(string stride)
    {
        Assert.That(Validate(Kind, Baseline, Target, Floor, (AssertStep.StrideArg, stride))[0],
            Does.Contain(AssertStep.StrideArg));
    }

    [Test]
    public void RejectsANegativeBound()
    {
        Assert.That(Validate(Kind, Baseline, Target, (AssertStep.MinDeltaEArg, "-1"))[0],
            Does.Contain(AssertStep.MinDeltaEArg));
    }

    [Test]
    public void RejectsAnImpossibleBand()
    {
        Assert.That(
            Validate(Kind, Baseline, Target, (AssertStep.MinDeltaEArg, "10"), (AssertStep.MaxDeltaEArg, "2"))[0],
            Does.Contain("no measurement can satisfy both"));
    }

    [Test]
    public void DefaultsAreTheDocumentedOnes()
    {
        Dictionary<string, string> empty = new Dictionary<string, string>();

        Assert.Multiple(() =>
        {
            Assert.That(AssertStep.ReadRegion(empty), Is.EqualTo(AssertStep.FullRegion));
            Assert.That(AssertStep.ReadStride(empty), Is.EqualTo(AssertStep.DefaultStride));
            Assert.That(AssertStep.ReadDirection(empty), Is.EqualTo(AssertStep.AnyDirection));
            Assert.That(AssertStep.TryReadFloat(empty, AssertStep.MinDeltaEArg), Is.Null);
            Assert.That(AssertStep.TryReadFloat(empty, AssertStep.MaxDeltaEArg), Is.Null);
        });
    }

    // The same defaults a fresh DeltaAssert carries, so a packet built by hand and one built from an
    // argless step describe the same measurement.
    [Test]
    public void StepDefaultsMatchTheReportModelDefaults()
    {
        DeltaAssert fresh = new DeltaAssert();
        Dictionary<string, string> empty = new Dictionary<string, string>();

        Assert.Multiple(() =>
        {
            Assert.That(AssertStep.ReadRegion(empty), Is.EqualTo(fresh.Region));
            Assert.That(AssertStep.ReadStride(empty), Is.EqualTo(fresh.Stride));
            Assert.That(AssertStep.ReadDirection(empty), Is.EqualTo(fresh.Direction));
        });
    }
}

// DeltaAssert.Inputs exists because of a live failure: a scenario recorded one of the two values
// that determined its result, so two runs with identical recorded numbers rendered eightfold
// differently and the report had nothing to point at. These tests pin what "what produced this
// delta?" answers with.
[TestFixture]
public class DeltaInputsTests
{
    private static ScenarioStep Shot(string fileName) => new ScenarioStep
    {
        Type = StepArgs.ScreenshotType,
        Args = new Dictionary<string, string> { [StepArgs.ScreenshotFileName] = fileName },
    };

    private static ScenarioStep Step(string type, params (string, string)[] args)
    {
        ScenarioStep step = new ScenarioStep { Type = type };
        foreach ((string k, string v) in args)
            step.Args[k] = v;
        return step;
    }

    private static readonly ScenarioStep Feature =
        Step(StepArgs.SetFeatureType,
            (StepArgs.SetFeatureName, "purpleLight"), (StepArgs.SetFeatureEnabled, "true"));

    [Test]
    public void RecordsWhatWasDeclaredBetweenTheTwoFrames()
    {
        List<string> inputs = DeltaInputs.Between(
            new[] { Shot("off.png"), Feature, Shot("on.png") }, "off.png", "on.png");

        Assert.That(inputs, Has.Count.EqualTo(1));
        Assert.That(inputs[0], Is.EqualTo("SetFeature(enabled=true, featureName=purpleLight)"));
    }

    // What the two captures SHARE cannot explain how they differ, and a full step dump would bury
    // the two or three lines that can.
    [Test]
    public void IgnoresStepsOutsideThePair()
    {
        List<string> inputs = DeltaInputs.Between(
            new[]
            {
                Step(StepArgs.SetTimeType, (StepArgs.SetTimeHour, "20")),
                Shot("off.png"),
                Feature,
                Shot("on.png"),
                Step(StepArgs.SetSeasonType, (StepArgs.SetSeasonDayOfYear, "30")),
            },
            "off.png", "on.png");

        Assert.That(inputs, Has.Count.EqualTo(1));
        Assert.That(inputs[0], Does.Contain("SetFeature"));
    }

    // A Wait exists so a frame can settle and a Screenshot is the thing being compared; neither
    // tells a reader anything about why the two frames differ.
    [Test]
    public void DropsPlumbingSteps()
    {
        List<string> inputs = DeltaInputs.Between(
            new[]
            {
                Shot("off.png"),
                Step(StepArgs.WaitType, (StepArgs.WaitFrames, "5")),
                Feature,
                Shot("mid.png"),
                Shot("on.png"),
            },
            "off.png", "on.png");

        Assert.That(inputs, Has.Count.EqualTo(1));
        Assert.That(inputs[0], Does.Contain("SetFeature"));
    }

    // A real and interesting answer, not an empty result: it means any measured difference came from
    // somewhere the scenario never asked for.
    [Test]
    public void SaysSoWhenNothingWasDeclaredBetweenTheFrames()
    {
        List<string> inputs = DeltaInputs.Between(
            new[] { Shot("off.png"), Shot("on.png") }, "off.png", "on.png");

        Assert.That(inputs, Has.Count.EqualTo(1));
        Assert.That(inputs[0], Does.Contain("no steps declared"));
    }

    [Test]
    public void NamesAFrameItCouldNotFind()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                DeltaInputs.Between(new[] { Shot("on.png") }, "off.png", "on.png")[0],
                Does.Contain("off.png"));
            Assert.That(
                DeltaInputs.Between(new[] { Shot("off.png") }, "off.png", "on.png")[0],
                Does.Contain("on.png"));
        });
    }

    // Order matters: a target that only appears BEFORE the baseline is not the pair the scenario
    // described, and quietly measuring the reverse would invert every direction assertion.
    [Test]
    public void RequiresTheTargetToFollowTheBaseline()
    {
        Assert.That(
            DeltaInputs.Between(new[] { Shot("on.png"), Shot("off.png") }, "off.png", "on.png")[0],
            Does.Contain("after"));
    }

    // A report anyone diffs between builds must not move because a dictionary enumerated differently.
    [Test]
    public void ArgsAreOrderedByKey()
    {
        ScenarioStep step = Step("PlaceThings",
            (StepArgs.SceneOffset, "0,0"), (StepArgs.SceneDef, "Wall"), (StepArgs.SceneAnchor, "center"));

        List<string> inputs = DeltaInputs.Between(
            new[] { Shot("a.png"), step, Shot("b.png") }, "a.png", "b.png");

        Assert.That(inputs[0], Is.EqualTo("PlaceThings(anchor=center, def=Wall, offset=0,0)"));
    }

    [Test]
    public void ElidesAnOverlongArgValueRatherThanDroppingIt()
    {
        string huge = new string('x', AssertStep.DefaultLogLines * 10);
        List<string> inputs = DeltaInputs.Between(
            new[] { Shot("a.png"), Step("Probe", (StepArgs.ProbeName, huge)), Shot("b.png") },
            "a.png", "b.png");

        Assert.That(inputs[0], Does.Contain("..."));
        Assert.That(inputs[0].Length, Is.LessThan(huge.Length),
            "the reader needs to know the arg was set, not to re-read its whole body here");
    }

    [Test]
    public void CapsALongListAndSaysHowMuchItDropped()
    {
        List<ScenarioStep> steps = new List<ScenarioStep> { Shot("a.png") };
        for (int i = 0; i < DeltaInputs.MaxDescribed + 5; i++)
            steps.Add(Step(StepArgs.AdvanceTicksType, (StepArgs.AdvanceTicksTicks, i.ToString())));
        steps.Add(Shot("b.png"));

        List<string> inputs = DeltaInputs.Between(steps, "a.png", "b.png");

        Assert.That(inputs, Has.Count.EqualTo(DeltaInputs.MaxDescribed + 1));
        Assert.That(inputs[^1], Does.Contain("5 more"));
    }
}
