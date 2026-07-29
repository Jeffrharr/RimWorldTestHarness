using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Tests;

// LandInOrbit's offline half. Its validation is worth more than most steps' because the step it
// guards is the most expensive one in the harness — it generates a whole map — so a typo caught here
// costs a second instead of a couple of minutes of game boot plus map generation.
[TestFixture]
public class LandInOrbitStepTests
{
    private static List<string> Validate(Dictionary<string, string> args)
    {
        List<string> errors = new List<string>();
        StepValidator.ValidateAll(
            new[] { new ScenarioStep { Type = LandInOrbitStep.StepType, Args = args } }, errors);
        return errors;
    }

    // The one arg with no default, and deliberately so: orbits are stationary, so an unpinned
    // latitude is an unpinned sun path and every probe downstream measures a world nobody chose.
    [Test]
    public void RejectsAMissingLatitude()
    {
        List<string> errors = Validate(new Dictionary<string, string>());

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain(LandInOrbitStep.LatitudeArg));
    }

    [TestCase("91")]
    [TestCase("-90.5")]
    [TestCase("north")]
    public void RejectsAnOutOfRangeOrUnparsableLatitude(string latitude)
    {
        List<string> errors = Validate(new Dictionary<string, string>
        {
            [LandInOrbitStep.LatitudeArg] = latitude,
        });

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain(LandInOrbitStep.LatitudeArg));
    }

    [Test]
    public void RejectsAnOutOfRangeLongitude()
    {
        List<string> errors = Validate(new Dictionary<string, string>
        {
            [LandInOrbitStep.LatitudeArg] = "45",
            [LandInOrbitStep.LongitudeArg] = "200",
        });

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain(LandInOrbitStep.LongitudeArg));
    }

    // A zero or negative tolerance would make the step unsatisfiable on any real icosphere, which
    // would read as "this world has no orbit tiles" rather than "you asked for the impossible".
    [TestCase("0")]
    [TestCase("-1")]
    public void RejectsANonPositiveOffsetTolerance(string tolerance)
    {
        List<string> errors = Validate(new Dictionary<string, string>
        {
            [LandInOrbitStep.LatitudeArg] = "45",
            [LandInOrbitStep.MaxOffsetArg] = tolerance,
        });

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain(LandInOrbitStep.MaxOffsetArg));
    }

    [TestCase("10")]
    [TestCase("2000")]
    public void RejectsAMapSizeRimWorldWouldNotGenerate(string mapSize)
    {
        List<string> errors = Validate(new Dictionary<string, string>
        {
            [LandInOrbitStep.LatitudeArg] = "45",
            [LandInOrbitStep.MapSizeArg] = mapSize,
        });

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain(LandInOrbitStep.MapSizeArg));
    }

    // Args is a case-sensitive dictionary, so a misspelt key would otherwise fall back to the default
    // silently — and defaulting `latitude` is exactly what this step must never do.
    [Test]
    public void RejectsAnUnknownArg()
    {
        List<string> errors = Validate(new Dictionary<string, string>
        {
            [LandInOrbitStep.LatitudeArg] = "45",
            ["lattitude"] = "60",
        });

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("lattitude"));
    }

    [Test]
    public void AcceptsALatitudeAlone_AndDefaultsTheRest()
    {
        Dictionary<string, string> args = new() { [LandInOrbitStep.LatitudeArg] = "45.5" };

        Assert.That(Validate(args), Is.Empty);
        Assert.That(LandInOrbitStep.TryRead(args, out OrbitRequest request, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(request.Latitude, Is.EqualTo(45.5f));
            Assert.That(request.Longitude, Is.Null, "no longitude means any longitude, not zero");
            Assert.That(request.MaxOffsetDegrees, Is.EqualTo(OrbitTileSelection.DefaultMaxOffsetDegrees));
            Assert.That(request.MapSize, Is.EqualTo(0), "0 is the 'use the world's own size' sentinel");
            Assert.That(request.Unfog, Is.True);
        });
    }

    [Test]
    public void AcceptsAFullyPinnedRequest()
    {
        Dictionary<string, string> args = new()
        {
            [LandInOrbitStep.LatitudeArg] = "-33.25",
            [LandInOrbitStep.LongitudeArg] = "18",
            [LandInOrbitStep.MaxOffsetArg] = "12.5",
            [LandInOrbitStep.MapSizeArg] = "150",
            [LandInOrbitStep.UnfogArg] = "false",
        };

        Assert.That(Validate(args), Is.Empty);
        Assert.That(LandInOrbitStep.TryRead(args, out OrbitRequest request, out _), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(request.Latitude, Is.EqualTo(-33.25f));
            Assert.That(request.Longitude, Is.EqualTo(18f));
            Assert.That(request.MaxOffsetDegrees, Is.EqualTo(12.5f));
            Assert.That(request.MapSize, Is.EqualTo(150));
            Assert.That(request.Unfog, Is.False);
        });
    }

    // The residue split is the isolation policy, and this step is the most dangerous case yet: it
    // does not dirty the map a following scenario would open on, it replaces it.
    [Test]
    public void DeclaresResidueOnlyAReloadCanUndo()
    {
        ScenarioResidue residue = ScenarioResidueAnalyzer.OfStep(LandInOrbitStep.StepType);

        Assert.Multiple(() =>
        {
            Assert.That(residue & ScenarioResidue.NewMap, Is.EqualTo(ScenarioResidue.NewMap));
            Assert.That(residue & ScenarioResidue.Latitude, Is.EqualTo(ScenarioResidue.Latitude),
                "it pins ForcedLatitude, which outlives the scenario unless reset");
            Assert.That(ScenarioResidueAnalyzer.SoftResettable & ScenarioResidue.NewMap,
                Is.EqualTo(ScenarioResidue.None), "no soft reset can un-generate a map");
        });
    }
}
