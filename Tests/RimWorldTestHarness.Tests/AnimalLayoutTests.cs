using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// Offline coverage of the pure SpawnAnimal planner: kind requirement, count/spacing grammar, the
// caps, and anchor reuse. No game is loaded — whether a kind resolves to a real animal is the
// adapter's job (SceneBuilder.SpawnAnimals), so it is deliberately not exercised here.
[TestFixture]
public class AnimalLayoutTests
{
    private static Dictionary<string, string> Args(params (string Key, string Value)[] args)
    {
        Dictionary<string, string> bag = new Dictionary<string, string>();
        foreach ((string key, string value) in args)
            bag[key] = value;
        return bag;
    }

    // Every valid plan needs a kind, so the happy-path helpers supply one and the missing-kind case
    // gets its own test.
    private static Dictionary<string, string> KindArgs(params (string Key, string Value)[] args)
    {
        Dictionary<string, string> bag = Args(args);
        if (!bag.ContainsKey(StepArgs.SpawnAnimalKind))
            bag[StepArgs.SpawnAnimalKind] = "Muffalo";
        return bag;
    }

    private static AnimalPlan Plan(params (string Key, string Value)[] args)
    {
        Assert.That(AnimalLayout.TryPlan(KindArgs(args), out AnimalPlan plan, out string? error), Is.True,
            $"expected a valid plan, got error: {error}");
        return plan;
    }

    private static string PlanError(Dictionary<string, string> args)
    {
        Assert.That(AnimalLayout.TryPlan(args, out _, out string? error), Is.False,
            "expected the plan to be rejected");
        Assert.That(error, Is.Not.Null);
        return error!;
    }

    private static IEnumerable<string> Offsets(AnimalPlan plan) =>
        plan.Cells.Select(c => $"{c.Dx},{c.Dz}");

    // ----- defaults ---------------------------------------------------------------------------

    // The common case — "spawn a muffalo at centre" — is bare args: one animal, on the anchor cell,
    // fog lifted so it is actually visible.
    [Test]
    public void Plan_BareArgs_IsOneAnimalAtCentre()
    {
        AnimalPlan plan = Plan();

        Assert.That(plan.KindDefName, Is.EqualTo("Muffalo"));
        Assert.That(Offsets(plan), Is.EqualTo(new[] { "0,0" }));
        Assert.That(plan.Anchor, Is.EqualTo(SceneAnchorKind.MapCenter));
        Assert.That(plan.Unfog, Is.True);
    }

    // ----- kind requirement -------------------------------------------------------------------

    [Test]
    public void Plan_MissingKind_IsRejected()
    {
        // Args() without the helper's default kind.
        string error = PlanError(Args((StepArgs.SpawnAnimalCount, "2")));
        Assert.That(error, Does.Contain(StepArgs.SpawnAnimalKind));
    }

    [Test]
    public void Plan_BlankKind_IsRejected()
    {
        string error = PlanError(Args((StepArgs.SpawnAnimalKind, "   ")));
        Assert.That(error, Does.Contain(StepArgs.SpawnAnimalKind));
    }

    // ----- count and spacing ------------------------------------------------------------------

    // Count lays animals in a row along +x, spacing cells apart, starting at the anchor.
    [Test]
    public void Plan_CountAndSpacing_LaysARowAlongX()
    {
        AnimalPlan plan = Plan(
            (StepArgs.SpawnAnimalCount, "3"),
            (StepArgs.SpawnAnimalSpacing, "2"));

        Assert.That(Offsets(plan), Is.EqualTo(new[] { "0,0", "2,0", "4,0" }));
    }

    // Spacing defaults to 2 so bodies don't overlap when count is omitted-but-multiple.
    [Test]
    public void Plan_DefaultSpacing_IsTwo()
    {
        AnimalPlan plan = Plan((StepArgs.SpawnAnimalCount, "2"));

        Assert.That(Offsets(plan), Is.EqualTo(new[] { "0,0", "2,0" }));
    }

    [Test]
    public void Plan_ZeroCount_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnAnimalCount, "0")));
        Assert.That(error, Does.Contain(StepArgs.SpawnAnimalCount));
    }

    [Test]
    public void Plan_CountOverCap_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnAnimalCount, (AnimalLayout.MaxCount + 1).ToString())));
        Assert.That(error, Does.Contain(StepArgs.SpawnAnimalCount));
    }

    [Test]
    public void Plan_CountAtCap_IsAccepted()
    {
        AnimalPlan plan = Plan((StepArgs.SpawnAnimalCount, AnimalLayout.MaxCount.ToString()));
        Assert.That(plan.Cells, Has.Count.EqualTo(AnimalLayout.MaxCount));
    }

    [Test]
    public void Plan_NonNumericCount_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnAnimalCount, "lots")));
        Assert.That(error, Does.Contain(StepArgs.SpawnAnimalCount));
    }

    // ----- anchor reuse -----------------------------------------------------------------------

    // Anchor/offset go through the same SceneLayout grammar PlaceThings uses, so an absolute anchor
    // plus an offset resolves the same way here.
    [Test]
    public void Plan_AbsoluteAnchorAndOffset_AreCarried()
    {
        AnimalPlan plan = Plan(
            (StepArgs.SceneAnchor, "100,120"),
            (StepArgs.SceneOffset, "5,-3"));

        Assert.That(plan.Anchor, Is.EqualTo(SceneAnchorKind.Absolute));
        Assert.That(plan.AnchorX, Is.EqualTo(100));
        Assert.That(plan.AnchorZ, Is.EqualTo(120));
        Assert.That(plan.OffsetX, Is.EqualTo(5));
        Assert.That(plan.OffsetZ, Is.EqualTo(-3));
    }

    // ----- unknown args -----------------------------------------------------------------------

    // A typo'd key must fail loudly rather than fall back to a default and produce a plausible-but-
    // wrong spawn — the whole reason ArgReader rejects unknown keys.
    [Test]
    public void Plan_UnknownArg_IsRejected()
    {
        string error = PlanError(KindArgs(("kindd", "Muffalo")));
        Assert.That(error, Does.Contain("kindd"));
    }

    [Test]
    public void Plan_UnfogFalse_IsCarried()
    {
        AnimalPlan plan = Plan((StepArgs.SceneUnfog, "false"));
        Assert.That(plan.Unfog, Is.False);
    }

    // ----- clear ------------------------------------------------------------------------------

    // Clearing is opt-in, like PlaceThings/SetTerrain: bare args must not permanently destroy map
    // content the scenario didn't ask to remove.
    [Test]
    public void Plan_ClearDefaultsToFalse()
    {
        AnimalPlan plan = Plan();
        Assert.That(plan.Clear, Is.False);
    }

    [Test]
    public void Plan_ClearTrue_IsCarried()
    {
        AnimalPlan plan = Plan((StepArgs.SceneClear, "true"));
        Assert.That(plan.Clear, Is.True);
    }

    [Test]
    public void Plan_NonBoolClear_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SceneClear, "yes")));
        Assert.That(error, Does.Contain(StepArgs.SceneClear));
    }
}
