using System;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class ScenarioResidueTests
{
    [TestCase(StepArgs.SetTileType, ScenarioResidue.Latitude)]
    [TestCase(StepArgs.SetSeasonType, ScenarioResidue.Clock)]
    [TestCase(StepArgs.SetTimeType, ScenarioResidue.Clock)]
    [TestCase(StepArgs.FastForwardType, ScenarioResidue.Clock | ScenarioResidue.TimeSpeed)]
    [TestCase(StepArgs.SetFeatureType, ScenarioResidue.FeatureFlags)]
    [TestCase(StepArgs.LookAtType, ScenarioResidue.Camera)]
    [TestCase(StepArgs.ScreenshotType, ScenarioResidue.ScreenshotMode)]
    [TestCase(StepArgs.PlaceThingsType, ScenarioResidue.Map)]
    [TestCase(StepArgs.SetTerrainType, ScenarioResidue.Map)]
    [TestCase(StepArgs.ProbeType, ScenarioResidue.None)]
    [TestCase(StepArgs.WaitType, ScenarioResidue.None)]
    public void OfStep_ClassifiesEachKnownStepType(string stepType, ScenarioResidue expected)
    {
        Assert.That(ScenarioResidueAnalyzer.OfStep(stepType), Is.EqualTo(expected));
    }

    // Conservative by design: a step type added to StepExecutor and forgotten here degrades into
    // "reload between everything" (slow but correct) rather than "share the world" (fast and wrong).
    [Test]
    public void OfStep_TreatsUnknownTypeAsDirtyingEverything()
    {
        Assert.That(ScenarioResidueAnalyzer.OfStep("SomeFutureStep"), Is.EqualTo(ScenarioResidue.All));
    }

    // A Timelapse only reaches the analyzer when its args failed validation, so it must not look
    // cleaner than the SetTime/Wait/Screenshot sequence it would have expanded to.
    [Test]
    public void OfStep_UnexpandedTimelapseClaimsItsExpansionsResidue()
    {
        Assert.That(ScenarioResidueAnalyzer.OfStep(StepArgs.TimelapseType),
            Is.EqualTo(ScenarioResidue.Clock | ScenarioResidue.ScreenshotMode));
    }

    // Every step type StepValidator accepts must be classified deliberately, i.e. must not fall into
    // the unknown-type default. This is the test that fails when a new step type is added without
    // deciding what it leaves behind.
    [Test]
    public void OfStep_EveryValidStepTypeIsClassifiedDeliberately()
    {
        string[] known =
        {
            StepArgs.SetTileType, StepArgs.SetSeasonType, StepArgs.SetTimeType, StepArgs.FastForwardType,
            StepArgs.ProbeType, StepArgs.ScreenshotType, StepArgs.SetFeatureType, StepArgs.WaitType,
            StepArgs.PlaceThingsType, StepArgs.SetTerrainType, StepArgs.LookAtType, StepArgs.TimelapseType,
        };

        Assert.Multiple(() =>
        {
            foreach (string type in known)
                Assert.That(ScenarioResidueAnalyzer.OfStep(type), Is.Not.EqualTo(ScenarioResidue.All),
                    $"step type '{type}' falls through to the unknown-type default — classify it in ScenarioResidueAnalyzer.OfStep");
        });
    }

    [Test]
    public void OfSteps_UnionsEveryStepsResidue()
    {
        var steps = new List<ScenarioStep>
        {
            new() { Type = StepArgs.SetTileType },
            new() { Type = StepArgs.SetTerrainType },
            new() { Type = StepArgs.ProbeType },
        };

        Assert.That(ScenarioResidueAnalyzer.OfSteps(steps),
            Is.EqualTo(ScenarioResidue.Latitude | ScenarioResidue.Map));
    }

    [Test]
    public void OfSteps_EmptyScenarioLeavesNothingBehind()
    {
        Assert.That(ScenarioResidueAnalyzer.OfSteps(new List<ScenarioStep>()), Is.EqualTo(ScenarioResidue.None));
    }

    // Map is the one residue a soft reset cannot undo — that split IS the isolation policy, so it is
    // pinned rather than left implied by SuitePlanner's behaviour.
    [Test]
    // The two halves must partition ScenarioResidue.All exactly: every flag is either something
    // WorldStateReset can put back or something only a reload can. A flag in neither would be
    // silently ignored by the isolation planner, which is the one failure mode that makes a suite
    // look isolated while it isn't.
    public void SoftResettable_AndRequiresReload_PartitionAll()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScenarioResidueAnalyzer.SoftResettable & ScenarioResidueAnalyzer.RequiresReload,
                Is.EqualTo(ScenarioResidue.None), "a flag cannot be both soft-resettable and reload-only");
            Assert.That(ScenarioResidueAnalyzer.SoftResettable | ScenarioResidueAnalyzer.RequiresReload,
                Is.EqualTo(ScenarioResidue.All), "every flag must be classified");
            // Named explicitly so adding a reload-only flag is a deliberate act with a test to update,
            // not something that slips in unnoticed.
            Assert.That(ScenarioResidueAnalyzer.RequiresReload,
                Is.EqualTo(ScenarioResidue.Map | ScenarioResidue.GameConditions
                    | ScenarioResidue.Weather | ScenarioResidue.Biome | ScenarioResidue.NewMap));
        });
    }

    [Test]
    public void Describe_NamesEveryFlagSet()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScenarioResidueAnalyzer.Describe(ScenarioResidue.None), Is.EqualTo("nothing"));
            Assert.That(ScenarioResidueAnalyzer.Describe(ScenarioResidue.Map | ScenarioResidue.Clock),
                Is.EqualTo("map, clock"));

            // Actually check every flag, rather than spot-checking one label as this used to. A flag
            // added to the enum but not to Describe's list would otherwise slip through silently,
            // and the symptom is a suite's isolation notes under-reporting what a scenario dirtied —
            // exactly the kind of quiet shortfall the reporting rules here exist to prevent.
            foreach (ScenarioResidue flag in Enum.GetValues(typeof(ScenarioResidue)))
            {
                bool isSingleFlag = flag != ScenarioResidue.None && flag != ScenarioResidue.All;
                if (isSingleFlag)
                {
                    Assert.That(ScenarioResidueAnalyzer.Describe(flag), Is.Not.EqualTo("nothing"),
                        $"{flag} has no label in Describe()");
                }
            }
        });
    }
}
