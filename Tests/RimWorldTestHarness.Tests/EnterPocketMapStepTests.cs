using System.Collections.Generic;
using NUnit.Framework;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Tests;

// Offline half of EnterPocketMap. The interesting half — that the generated map really has no world
// tile — can only be asserted in a running game, and is, by
// EnterPocketMapAction.VerifyReallyAPocketMap. What is checkable here is the arg contract, and the
// reason it is worth checking is that every failure below is one a scenario author makes at 2am and
// would otherwise discover after a full game boot.
[TestFixture]
public class EnterPocketMapStepTests
{
    private static bool Validate(Dictionary<string, string> args, out string? error) =>
        new EnterPocketMapStep().TryValidate(args, out error);

    [Test]
    public void MapGenerator_IsRequired()
    {
        Assert.That(Validate(new Dictionary<string, string>(), out string? error), Is.False);
        Assert.That(error, Does.Contain(EnterPocketMapStep.MapGeneratorArg));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void MapGenerator_RejectsBlank(string defName)
    {
        var args = new Dictionary<string, string> { [EnterPocketMapStep.MapGeneratorArg] = defName };

        Assert.That(Validate(args, out string? error), Is.False);
        Assert.That(error, Does.Contain(EnterPocketMapStep.MapGeneratorArg));
    }

    [Test]
    public void MapGenerator_AloneIsEnough()
    {
        var args = new Dictionary<string, string>
        {
            [EnterPocketMapStep.MapGeneratorArg] = "Labyrinth",
        };

        Assert.That(Validate(args, out string? error), Is.True);
        Assert.That(error, Is.Null);
    }

    // The defName is deliberately NOT resolved offline — there is no DefDatabase here — so a
    // plausible-looking but nonexistent generator has to pass validation and fail in the action. This
    // pins that split so nobody "fixes" it by adding a name list that would go stale against mods.
    [Test]
    public void MapGenerator_IsNotResolvedOffline()
    {
        var args = new Dictionary<string, string>
        {
            [EnterPocketMapStep.MapGeneratorArg] = "NoSuchGeneratorAnywhere",
        };

        Assert.That(Validate(args, out _), Is.True);
    }

    [Test]
    public void Size_DefaultsWhenAbsent()
    {
        var args = new Dictionary<string, string>
        {
            [EnterPocketMapStep.MapGeneratorArg] = "Labyrinth",
        };

        Assert.That(EnterPocketMapStep.TryReadSize(args, out int size, out _), Is.True);
        Assert.That(size, Is.EqualTo(EnterPocketMapStep.DefaultSize));
    }

    [Test]
    public void Size_IsReadWhenPresent()
    {
        var args = new Dictionary<string, string>
        {
            [EnterPocketMapStep.MapGeneratorArg] = "Labyrinth",
            [EnterPocketMapStep.SizeArg] = " 90 ",
        };

        Assert.That(EnterPocketMapStep.TryReadSize(args, out int size, out _), Is.True);
        Assert.That(size, Is.EqualTo(90), "the obelisk's own labyrinth size, and whitespace-tolerant");
    }

    [TestCase("ninety")]
    [TestCase("90.5")]
    public void Size_RejectsNonInteger(string raw)
    {
        var args = new Dictionary<string, string>
        {
            [EnterPocketMapStep.MapGeneratorArg] = "Labyrinth",
            [EnterPocketMapStep.SizeArg] = raw,
        };

        Assert.That(Validate(args, out string? error), Is.False);
        Assert.That(error, Does.Contain(EnterPocketMapStep.SizeArg));
    }

    // Below RimWorld's own map-size minimum the generator does not throw; it produces something the
    // layout genstep cannot fill, which photographs as an empty box on a green run. Caught at load
    // instead.
    [TestCase("49")]
    [TestCase("0")]
    [TestCase("-90")]
    public void Size_RejectsBelowRimWorldsMinimum(string raw)
    {
        var args = new Dictionary<string, string>
        {
            [EnterPocketMapStep.MapGeneratorArg] = "Labyrinth",
            [EnterPocketMapStep.SizeArg] = raw,
        };

        Assert.That(Validate(args, out string? error), Is.False);
        Assert.That(error, Does.Contain(EnterPocketMapStep.SizeArg));
    }

    // Residue is the dangerous property (CONTRIBUTING.md). NewMap is not soft-resettable, so a suite
    // reloads after this step rather than opening the next scenario inside a labyrinth.
    [Test]
    public void Residue_IsNewMapOnly()
    {
        Assert.That(new EnterPocketMapStep().Residue,
            Is.EqualTo(RimWorldTestHarness.Shared.ScenarioResidue.NewMap));
    }

    [Test]
    public void IsNotLiveCallable()
    {
        Assert.That(new EnterPocketMapStep().LiveCallable, Is.False,
            "it generates a map into a real player's save and moves them to it");
    }
}
