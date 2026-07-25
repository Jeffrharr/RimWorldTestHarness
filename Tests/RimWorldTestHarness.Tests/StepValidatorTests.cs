using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class StepValidatorTests
{
    private static List<string> Validate(params ScenarioStep[] steps)
    {
        List<string> errors = new List<string>();
        StepValidator.ValidateAll(steps, errors);
        return errors;
    }

    private static ScenarioStep Step(string type, params (string Key, string Value)[] args)
    {
        ScenarioStep step = new ScenarioStep { Type = type };
        foreach ((string key, string value) in args)
            step.Args[key] = value;
        return step;
    }

    [Test]
    public void Validate_KnownStepsWithValidArgs_ProducesNoErrors()
    {
        List<string> errors = Validate(
            Step(StepArgs.SetTileType, (StepArgs.SetTileLatitude, "55")),
            Step(StepArgs.SetTerrainType, (StepArgs.SceneDef, "Concrete")),
            Step(StepArgs.PlaceThingsType, (StepArgs.SceneDef, "Wall")),
            Step(StepArgs.LookAtType, (StepArgs.LookAtZoom, "28")),
            Step(StepArgs.ScreenshotType, (StepArgs.ScreenshotFileName, "x.png")));

        Assert.That(errors, Is.Empty);
    }

    // ScenarioSpec has always documented that the loader validates step types; until StepValidator
    // existed nothing did, and a typo surfaced only as an executed-step error deep into a run.
    [Test]
    public void Validate_UnknownStepType_IsReportedWithIndex()
    {
        List<string> errors = Validate(
            Step(StepArgs.SetTileType, (StepArgs.SetTileLatitude, "55")),
            Step("Screenshit", (StepArgs.ScreenshotFileName, "x.png")));

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("step 1 has unknown type 'Screenshit'"));
    }

    // The index is the only way to point at a step: steps have no names, and a full-day Timelapse
    // expands to hundreds of them.
    [Test]
    public void Validate_BadSceneArgs_AreReportedWithIndexAndStepType()
    {
        List<string> errors = Validate(
            Step(StepArgs.SetTileType, (StepArgs.SetTileLatitude, "55")),
            Step(StepArgs.PlaceThingsType, (StepArgs.SceneDef, "Wall"), (StepArgs.PlaceThingsCols, "0")));

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("step 1 (PlaceThings) is invalid"));
        Assert.That(errors[0], Does.Contain("must be at least 1"));
    }

    [Test]
    public void Validate_EverySceneStepType_IsChecked()
    {
        List<string> errors = Validate(
            Step(StepArgs.PlaceThingsType),
            Step(StepArgs.SetTerrainType),
            Step(StepArgs.LookAtType, (StepArgs.LookAtZoom, "0")));

        Assert.That(errors, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(errors[0], Does.Contain("step 0 (PlaceThings)"));
            Assert.That(errors[1], Does.Contain("step 1 (SetTerrain)"));
            Assert.That(errors[2], Does.Contain("step 2 (LookAt)"));
        });
    }

    // Steps the validator has no opinion about must pass through untouched — it is not a
    // second implementation of every step's arg contract, only of the scene-setup ones.
    [Test]
    public void Validate_NonSceneStepArgs_AreNotInspected()
    {
        List<string> errors = Validate(
            Step(StepArgs.SetTileType, ("nonsense", "whatever")),
            Step(StepArgs.WaitType, (StepArgs.WaitFrames, "-5")));

        Assert.That(errors, Is.Empty);
    }

    // A Timelapse only survives expansion when its own args failed, and TimelapseExpander already
    // recorded the specific reason — piling "unknown type" on top would be noise.
    [Test]
    public void Validate_UnexpandedTimelapse_IsNotReportedAsUnknownType()
    {
        List<string> errors = Validate(Step(StepArgs.TimelapseType, (StepArgs.TimelapseStepHours, "0")));

        Assert.That(errors, Is.Empty);
    }
}
