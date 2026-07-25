using System.Collections.Generic;
using NUnit.Framework;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Tests;

// The vision tier's policy, which is the part worth getting exactly right: it decides when an LLM's
// opinion is allowed to fail a build.
[TestFixture]
public class VisionGateTests
{
    private static VisionAssert Assert_(float gate = 0.7f, bool? pass = null, float confidence = 0f) =>
        new VisionAssert
        {
            Id = "a",
            Prompt = "is the second frame brighter?",
            ConfidenceGate = gate,
            Verdict = pass == null
                ? null
                : new VisionVerdict { Pass = pass.Value, Confidence = confidence, Reason = "because" },
        };

    // The whole truth table, because every cell here is a decision someone could reasonably argue
    // the other way, and quietly flipping one changes what a green build means.
    [Test]
    public void Classify_CoversEveryCombination()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VisionGate.Classify(Assert_()), Is.EqualTo(VisionOutcome.Pending),
                "no verdict means nobody has looked — never confuse that with approval");
            Assert.That(VisionGate.Classify(Assert_(pass: false, confidence: 0.9f)), Is.EqualTo(VisionOutcome.Blocked),
                "a confident fail is the one thing worth failing a run over");
            Assert.That(VisionGate.Classify(Assert_(pass: true, confidence: 0.9f)), Is.EqualTo(VisionOutcome.Passed));
            Assert.That(VisionGate.Classify(Assert_(pass: false, confidence: 0.5f)), Is.EqualTo(VisionOutcome.NeedsHuman),
                "an unsure fail asks for a human rather than red-building");
            Assert.That(VisionGate.Classify(Assert_(pass: true, confidence: 0.5f)), Is.EqualTo(VisionOutcome.NeedsHuman),
                "an unsure pass is not an approval either");
        });
    }

    // Exactly at the gate counts as confident. Stated as its own test because an off-by-one here
    // silently changes the meaning of every confidenceGate anyone has already written.
    [Test]
    public void Classify_ConfidenceExactlyAtTheGateIsConfident()
    {
        Assert.That(VisionGate.Classify(Assert_(gate: 0.7f, pass: false, confidence: 0.7f)),
            Is.EqualTo(VisionOutcome.Blocked));
    }

    [Test]
    public void OnlyAConfidentFailBlocksTheRun()
    {
        List<ProbeCheckResult> noProbes = new List<ProbeCheckResult>();
        List<string> noErrors = new List<string>();

        Assert.Multiple(() =>
        {
            Assert.That(ReportComparer.AllPass(noProbes, noErrors, new[] { Assert_() }), Is.True,
                "an unjudged rubric leaves the run provisionally green");
            Assert.That(ReportComparer.AllPass(noProbes, noErrors, new[] { Assert_(pass: true, confidence: 0.9f) }), Is.True);
            Assert.That(ReportComparer.AllPass(noProbes, noErrors, new[] { Assert_(pass: false, confidence: 0.5f) }), Is.True,
                "an unsure fail must not red-build, or the gate gets switched off");
            Assert.That(ReportComparer.AllPass(noProbes, noErrors, new[] { Assert_(pass: false, confidence: 0.9f) }), Is.False);
        });
    }

    // A vision pass must never rescue a run that failed for a real reason.
    [Test]
    public void AConfidentVisionPassDoesNotOverrideProbeOrErrorFailures()
    {
        VisionAssert[] approved = { Assert_(pass: true, confidence: 1f) };
        ProbeCheckResult failedProbe = new ProbeCheckResult { ProbeName = "p", Pass = false };

        Assert.Multiple(() =>
        {
            Assert.That(ReportComparer.AllPass(new[] { failedProbe }, new List<string>(), approved), Is.False);
            Assert.That(ReportComparer.AllPass(new List<ProbeCheckResult>(), new[] { "boom" }, approved), Is.False);
        });
    }

    [Test]
    public void PendingAndNeedsHumanAreCountedSeparately()
    {
        VisionAssert[] asserts =
        {
            Assert_(),
            Assert_(),
            Assert_(pass: true, confidence: 0.5f),
            Assert_(pass: true, confidence: 0.99f),
        };

        Assert.Multiple(() =>
        {
            Assert.That(VisionGate.PendingCount(asserts), Is.EqualTo(2));
            Assert.That(VisionGate.NeedsHumanCount(asserts), Is.EqualTo(1));
            Assert.That(VisionGate.ReviewComplete(asserts), Is.False);
            Assert.That(VisionGate.Describe(asserts), Is.EqualTo("2 pending review, 1 needs a human, 1 passed"));
        });
    }

    [Test]
    public void ReviewCompleteOnlyWhenEveryAssertWasJudgedConfidently()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VisionGate.ReviewComplete(new[] { Assert_(pass: true, confidence: 0.9f) }), Is.True);
            Assert.That(VisionGate.ReviewComplete(new[] { Assert_(pass: false, confidence: 0.9f) }), Is.True,
                "a confident fail is still a completed review");
            Assert.That(VisionGate.ReviewComplete(new[] { Assert_() }), Is.False);
        });
    }

    // No asserts must read as "nothing to say", not as an empty review summary, so scenarios without
    // rubrics print exactly what they always did.
    [Test]
    public void DescribeIsEmptyWhenThereAreNoAsserts()
    {
        Assert.That(VisionGate.Describe(new List<VisionAssert>()), Is.Empty);
    }
}

[TestFixture]
public class AssertStepTests
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

    private static (string, string) Kind => (AssertStep.KindArg, AssertStep.VisionKind);
    private static (string, string) Images => (AssertStep.ImagesArg, "a.png,b.png");
    private static (string, string) Prompt => (AssertStep.PromptArg, "is b brighter than a?");

    [Test]
    public void AcceptsAMinimalVisionAssert()
    {
        Assert.That(Validate(Kind, Images, Prompt), Is.Empty);
    }

    [Test]
    public void RequiresKindImagesAndPrompt()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Validate(Images, Prompt)[0], Does.Contain(AssertStep.KindArg));
            Assert.That(Validate(Kind, Prompt)[0], Does.Contain(AssertStep.ImagesArg));
            Assert.That(Validate(Kind, Images)[0], Does.Contain(AssertStep.PromptArg));
        });
    }

    // A 'delta' assert must be rejected loudly rather than accepted and ignored: a scenario that
    // looks like it asserts something and doesn't is the exact failure this tier exists to catch.
    [Test]
    public void RejectsTheUnimplementedDeltaKindByName()
    {
        List<string> errors = Validate((AssertStep.KindArg, AssertStep.DeltaKind), Images, Prompt);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("not implemented"));
    }

    [Test]
    public void RejectsAnUnknownKind()
    {
        Assert.That(Validate((AssertStep.KindArg, "vibes"), Images, Prompt)[0], Does.Contain("vibes"));
    }

    [TestCase("-0.1")]
    [TestCase("1.5")]
    [TestCase("very")]
    public void RejectsAConfidenceGateOutsideZeroToOne(string gate)
    {
        Assert.That(Validate(Kind, Images, Prompt, (AssertStep.ConfidenceGateArg, gate))[0],
            Does.Contain(AssertStep.ConfidenceGateArg));
    }

    [Test]
    public void RejectsACommaOnlyImagesList()
    {
        Assert.That(Validate(Kind, (AssertStep.ImagesArg, " , , "), Prompt)[0],
            Does.Contain(AssertStep.ImagesArg));
    }

    [Test]
    public void RejectsANegativeLogLines()
    {
        Assert.That(Validate(Kind, Images, Prompt, (AssertStep.LogLinesArg, "-1"))[0],
            Does.Contain(AssertStep.LogLinesArg));
    }

    [Test]
    public void DefaultsAreTheDocumentedOnes()
    {
        Dictionary<string, string> empty = new Dictionary<string, string>();

        Assert.Multiple(() =>
        {
            Assert.That(AssertStep.ReadConfidenceGate(empty), Is.EqualTo(VisionAssert.DefaultConfidenceGate));
            Assert.That(AssertStep.ReadLogLines(empty), Is.EqualTo(AssertStep.DefaultLogLines));
        });
    }

    // Read-only and not live-callable: an Assert's only output is a report entry, so exposing it on
    // the companion channel would produce a verb that silently does nothing.
    [Test]
    public void LeavesNoResidueAndIsNotLiveCallable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScenarioResidueAnalyzer.OfStep(AssertStep.StepType), Is.EqualTo(ScenarioResidue.None));
            Assert.That(StepRegistry.LiveCallableTypes, Does.Not.Contain(AssertStep.StepType));
        });
    }
}
