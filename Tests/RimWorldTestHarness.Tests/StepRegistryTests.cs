using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Tests;

// Covers the registry itself: that reflection discovery actually finds the built-ins with no
// bootstrap, and that the two properties a step gets wrong most dangerously — residue and
// live-callability — are pinned rather than trusted.
[TestFixture]
public class StepRegistryTests
{
    // Discovery is a static constructor over this assembly's own types, so simply touching the
    // registry from a test process with no game and no mod loader must be enough. If this fails,
    // offline validation of every scenario silently reports every step as an unknown type.
    [Test]
    public void Discovery_FindsEveryBuiltInStep()
    {
        string[] expected =
        {
            StepArgs.SetTileType, StepArgs.SetSeasonType, StepArgs.SetTimeType,
            StepArgs.FastForwardType, StepArgs.WaitType, StepArgs.ProbeType,
            StepArgs.ScreenshotType, StepArgs.SetFeatureType, StepArgs.StartConditionType,
            StepArgs.PlaceThingsType, StepArgs.SetTerrainType, StepArgs.SetRoofType,
            StepArgs.LookAtType,
            StepArgs.SpawnPawnType, StepArgs.TimelapseType, "SetWeather", "SetBiome", "Assert",
            LandInOrbitStep.StepType,
        };

        Assert.That(StepRegistry.KnownTypes, Is.EquivalentTo(expected));
    }

    [Test]
    public void Discovery_ReportsNoErrors()
    {
        Assert.That(StepRegistry.DiscoveryErrors, Is.Empty);
    }

    // The safety-critical list. The companion channel runs these against a REAL player's colony, so
    // anything that mutates the world must stay off it — scene setup spawns with WipeMode.Vanish and
    // would silently destroy part of someone's base. Pinned as an exact set so making a step
    // live-callable is a deliberate act that fails this test, rather than a default someone inherits.
    [Test]
    public void LiveCallable_IsExactlyTheNonMutatingSteps()
    {
        string[] expected =
        {
            StepArgs.SetTileType, StepArgs.SetSeasonType, StepArgs.SetTimeType,
            StepArgs.FastForwardType, StepArgs.ProbeType, StepArgs.ScreenshotType,
            StepArgs.SetFeatureType,
        };

        Assert.That(StepRegistry.LiveCallableTypes, Is.EquivalentTo(expected));
    }

    // Every world-mutating step must report residue a soft reset cannot undo, or a suite would run
    // the next scenario against a contaminated world while believing it was isolated.
    [TestCase(StepArgs.PlaceThingsType)]
    [TestCase(StepArgs.SetTerrainType)]
    [TestCase(StepArgs.SetRoofType)]
    [TestCase(StepArgs.SpawnPawnType)]
    [TestCase(StepArgs.StartConditionType)]
    [TestCase("SetWeather")]
    [TestCase("SetBiome")]
    [TestCase(LandInOrbitStep.StepType)]
    public void MutatingSteps_RequireAReload(string stepType)
    {
        ScenarioResidue residue = ScenarioResidueAnalyzer.OfStep(stepType);

        Assert.That(residue & ScenarioResidueAnalyzer.RequiresReload, Is.Not.EqualTo(ScenarioResidue.None),
            $"{stepType} mutates the world but claims residue a soft reset can undo");
    }

    // The regression this whole change was partly motivated by: StartCondition shipped without a case
    // in the old switch and fell to `default: All`, over-reporting residue it never produced.
    [Test]
    public void StartCondition_ReportsOnlyGameConditionResidue()
    {
        Assert.That(ScenarioResidueAnalyzer.OfStep(StepArgs.StartConditionType),
            Is.EqualTo(ScenarioResidue.GameConditions));
    }

    // The conservative default has to survive the move to a registry: an unregistered step must read
    // as maximally dirty, so a step whose spec failed to load degrades into "reload between
    // everything" (slow but correct) rather than "shares the world" (fast and wrong).
    [Test]
    public void UnknownStep_StillReportsWorstCaseResidue()
    {
        Assert.That(ScenarioResidueAnalyzer.OfStep("NoSuchStep"), Is.EqualTo(ScenarioResidue.All));
    }

    [Test]
    public void UnknownStep_IsRejectedAtLoadWithTheKnownTypesListed()
    {
        List<string> errors = new List<string>();
        StepValidator.ValidateAll(new[] { new ScenarioStep { Type = "NoSuchStep" } }, errors);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("NoSuchStep"));
        Assert.That(errors[0], Does.Contain(StepArgs.ProbeType), "the message should list what IS valid");
    }

    // Composites are the one legitimate reason for a spec to have no executing half, so the
    // spec/action consistency check has to know the difference. Timelapse is the built-in case.
    [Test]
    public void Timelapse_IsTheOnlyComposite()
    {
        var composites = StepRegistry.All.Where(s => s is IStepExpander).Select(s => s.Type);

        Assert.That(composites, Is.EquivalentTo(new[] { StepArgs.TimelapseType }));
    }

    // SetWeather is the worked example CONTRIBUTING.md points at, so its offline validation is what a
    // contributor copies. Worth pinning that it actually rejects a missing arg rather than deferring
    // everything to the game.
    [Test]
    public void SetWeather_RejectsAMissingDefNameAtLoad()
    {
        List<string> errors = new List<string>();
        StepValidator.ValidateAll(new[] { new ScenarioStep { Type = "SetWeather" } }, errors);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("weatherDef"));
    }

    [Test]
    public void SetWeather_RejectsANonBooleanInstantFlag()
    {
        List<string> errors = new List<string>();
        ScenarioStep step = new ScenarioStep
        {
            Type = "SetWeather",
            Args = { ["weatherDef"] = "Rain", ["instant"] = "yes-please" },
        };
        StepValidator.ValidateAll(new[] { step }, errors);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("instant"));
    }

    [Test]
    public void SetBiome_RejectsAMissingDefNameAtLoad()
    {
        List<string> errors = new List<string>();
        StepValidator.ValidateAll(new[] { new ScenarioStep { Type = "SetBiome" } }, errors);

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("biomeDef"));
    }
}
