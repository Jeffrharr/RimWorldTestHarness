using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Tests;

// Validation coverage for SetSnow. The live half (writing SnowGrid depths) needs a Map and is only
// exercised by a scenario run; everything a scenario author can get wrong is checked here, offline,
// before a run spends minutes producing frames of bare ground.
[TestFixture]
public class SetSnowStepTests
{
    private static bool Validate(out string? error, params (string Key, string Value)[] args)
    {
        Dictionary<string, string> dict = new Dictionary<string, string>();
        foreach ((string key, string value) in args)
            dict[key] = value;
        return new SetSnowStep().TryValidate(dict, out error);
    }

    [Test]
    public void Validate_NoArgs_IsAcceptedAsFullDepthOverTheDefaultRect()
    {
        // `{"type": "SetSnow"}` should mean "cover the backdrop", the overwhelmingly common case.
        Assert.That(Validate(out string? error), Is.True);
        Assert.That(error, Is.Null);
    }

    [TestCase("0")]
    [TestCase("0.35")]
    [TestCase("1")]
    public void Validate_DepthWithinRange_IsAccepted(string depth)
    {
        Assert.That(Validate(out _, ("depth", depth)), Is.True);
    }

    [TestCase("-0.1")]
    [TestCase("1.5")]
    [TestCase("100")]
    public void Validate_DepthOutsideRange_IsRejectedRatherThanClamped(string depth)
    {
        // Clamping would hide the likely mistake — thinking depth is a percentage or a cell count —
        // behind a screenshot that looks deliberate.
        Assert.That(Validate(out string? error, ("depth", depth)), Is.False);
        Assert.That(error, Does.Contain("between 0 and 1"));
    }

    [Test]
    public void Validate_ZeroOrNegativeExtent_IsRejected()
    {
        Assert.That(Validate(out string? error, ("width", "0")), Is.False);
        Assert.That(error, Does.Contain("at least 1"));
    }

    [Test]
    public void Validate_HugeRect_IsRejectedByTheCellCap()
    {
        Assert.That(Validate(out string? error, ("width", "500"), ("height", "500")), Is.False);
        Assert.That(error, Does.Contain($"{SceneLayout.MaxTerrainCells}-cell"));
    }

    [Test]
    public void Validate_OverflowingRect_IsCaughtByTheLongMultiply()
    {
        // Two large-but-legal ints whose int product wraps to a small positive number. Without the
        // long multiply this sails past the cap and hands the adapter a billion-cell rect.
        Assert.That(Validate(out string? error, ("width", "65536"), ("height", "65536")), Is.False);
        Assert.That(error, Does.Contain("cell"));
    }

    [Test]
    public void Validate_UnknownArg_IsRejected()
    {
        // Same discipline as the scene steps: an arg that looks honoured but isn't produces a
        // plausible-looking wrong scene.
        Assert.That(Validate(out string? error, ("radius", "10")), Is.False);
        Assert.That(error, Does.Contain("radius"));
    }

    [Test]
    public void Validate_AbsoluteAnchorAndOffset_AreReadWithTheSceneVocabulary()
    {
        Assert.That(Validate(out _, ("anchor", "125,125"), ("offset", "0,-19")), Is.True);
        Assert.That(Validate(out string? error, ("anchor", "-5,10")), Is.False);
        Assert.That(error, Does.Contain("non-negative"));
    }

    [Test]
    public void Step_IsMapResidueAndNotLiveCallable()
    {
        SetSnowStep step = new SetSnowStep();

        // Snow outlives its scenario and changes what every later frame is lit against, so a suite
        // must reload rather than soft-reset past it.
        Assert.That(step.Residue, Is.EqualTo(ScenarioResidue.Map));
        Assert.That(step.LiveCallable, Is.False);
    }

    [Test]
    public void Step_IsDiscoveredByTheRegistry()
    {
        // The registry finds steps by reflection, so a new step needs no registration call — this
        // asserts that actually held rather than trusting it.
        Assert.That(StepRegistry.KnownTypes, Does.Contain(SetSnowStep.StepType));
    }
}
