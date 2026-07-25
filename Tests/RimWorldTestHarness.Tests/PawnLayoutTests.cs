using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// Offline coverage of the pure SpawnPawn planner: kind requirement, faction/gender selectors, the
// hediff grammar, count/spacing, the caps, and anchor reuse. No game is loaded — whether a kind,
// faction, hediff or body part resolves is the adapter's job (SceneBuilder.SpawnPawns), so it is
// deliberately not exercised here.
[TestFixture]
public class PawnLayoutTests
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
        if (!bag.ContainsKey(StepArgs.SpawnPawnKind))
            bag[StepArgs.SpawnPawnKind] = "Muffalo";
        return bag;
    }

    private static PawnPlan Plan(params (string Key, string Value)[] args)
    {
        Assert.That(PawnLayout.TryPlan(KindArgs(args), out PawnPlan plan, out string? error), Is.True,
            $"expected a valid plan, got error: {error}");
        return plan;
    }

    private static string PlanError(Dictionary<string, string> args)
    {
        Assert.That(PawnLayout.TryPlan(args, out _, out string? error), Is.False,
            "expected the plan to be rejected");
        Assert.That(error, Is.Not.Null);
        return error!;
    }

    private static IEnumerable<string> Offsets(PawnPlan plan) =>
        plan.Cells.Select(c => $"{c.Dx},{c.Dz}");

    // ----- defaults ---------------------------------------------------------------------------

    // The common case — "spawn a muffalo at centre" — is bare args: one wild pawn, on the anchor cell,
    // fog lifted so it is actually visible, no forced gender or hediffs.
    [Test]
    public void Plan_BareArgs_IsOneWildPawnAtCentre()
    {
        PawnPlan plan = Plan();

        Assert.That(plan.KindDefName, Is.EqualTo("Muffalo"));
        Assert.That(Offsets(plan), Is.EqualTo(new[] { "0,0" }));
        Assert.That(plan.Anchor, Is.EqualTo(SceneAnchorKind.MapCenter));
        Assert.That(plan.Faction, Is.EqualTo(SpawnFaction.Wild));
        Assert.That(plan.Gender, Is.EqualTo(SpawnGender.Unset));
        Assert.That(plan.Hediffs, Is.Empty);
        Assert.That(plan.Unfog, Is.True);
        Assert.That(plan.Clear, Is.False);
    }

    // ----- kind requirement -------------------------------------------------------------------

    [Test]
    public void Plan_MissingKind_IsRejected()
    {
        string error = PlanError(Args((StepArgs.SpawnPawnCount, "2")));
        Assert.That(error, Does.Contain(StepArgs.SpawnPawnKind));
    }

    [Test]
    public void Plan_BlankKind_IsRejected()
    {
        string error = PlanError(Args((StepArgs.SpawnPawnKind, "   ")));
        Assert.That(error, Does.Contain(StepArgs.SpawnPawnKind));
    }

    // ----- faction ----------------------------------------------------------------------------

    [TestCase("wild", SpawnFaction.Wild)]
    [TestCase("player", SpawnFaction.Player)]
    [TestCase("hostile", SpawnFaction.Hostile)]
    public void Plan_Faction_IsParsed(string value, SpawnFaction expected)
    {
        PawnPlan plan = Plan((StepArgs.SpawnPawnFaction, value));
        Assert.That(plan.Faction, Is.EqualTo(expected));
    }

    // An unknown faction must fail rather than silently defaulting to wild — a hostile scenario that
    // quietly spawned neutral pawns would pass while testing nothing.
    [Test]
    public void Plan_UnknownFaction_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnPawnFaction, "enemy")));
        Assert.That(error, Does.Contain(StepArgs.SpawnPawnFaction));
    }

    // ----- gender -----------------------------------------------------------------------------

    [TestCase("male", SpawnGender.Male)]
    [TestCase("female", SpawnGender.Female)]
    public void Plan_Gender_IsParsed(string value, SpawnGender expected)
    {
        PawnPlan plan = Plan((StepArgs.SpawnPawnGender, value));
        Assert.That(plan.Gender, Is.EqualTo(expected));
    }

    [Test]
    public void Plan_UnknownGender_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnPawnGender, "other")));
        Assert.That(error, Does.Contain(StepArgs.SpawnPawnGender));
    }

    // ----- hediffs ----------------------------------------------------------------------------

    [Test]
    public void Plan_WholeBodyHediffWithSeverity_IsParsed()
    {
        PawnPlan plan = Plan((StepArgs.SpawnPawnHediffs, "Flu:0.4"));

        Assert.That(plan.Hediffs, Has.Count.EqualTo(1));
        Assert.That(plan.Hediffs[0].DefName, Is.EqualTo("Flu"));
        Assert.That(plan.Hediffs[0].BodyPartDefName, Is.Null);
        Assert.That(plan.Hediffs[0].Severity, Is.EqualTo(0.4f));
    }

    // The full grammar in one list: a whole-body condition, a part-targeted removal, and a
    // part-targeted implant, semicolon-separated with loose whitespace.
    [Test]
    public void Plan_MixedHediffList_IsParsed()
    {
        PawnPlan plan = Plan((StepArgs.SpawnPawnHediffs, "Flu:0.4; MissingBodyPart@Leg ; BionicArm@Arm"));

        Assert.That(plan.Hediffs, Has.Count.EqualTo(3));

        Assert.That(plan.Hediffs[1].DefName, Is.EqualTo("MissingBodyPart"));
        Assert.That(plan.Hediffs[1].BodyPartDefName, Is.EqualTo("Leg"));
        Assert.That(plan.Hediffs[1].Severity, Is.Null);

        Assert.That(plan.Hediffs[2].DefName, Is.EqualTo("BionicArm"));
        Assert.That(plan.Hediffs[2].BodyPartDefName, Is.EqualTo("Arm"));
    }

    // A bare def name with neither part nor severity is the simplest valid entry.
    [Test]
    public void Plan_BareHediff_IsParsed()
    {
        PawnPlan plan = Plan((StepArgs.SpawnPawnHediffs, "WoundInfection"));

        Assert.That(plan.Hediffs, Has.Count.EqualTo(1));
        Assert.That(plan.Hediffs[0].DefName, Is.EqualTo("WoundInfection"));
        Assert.That(plan.Hediffs[0].BodyPartDefName, Is.Null);
        Assert.That(plan.Hediffs[0].Severity, Is.Null);
    }

    [Test]
    public void Plan_NonNumericSeverity_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnPawnHediffs, "Flu:bad")));
        Assert.That(error, Does.Contain("severity"));
    }

    [Test]
    public void Plan_ZeroSeverity_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnPawnHediffs, "Flu:0")));
        Assert.That(error, Does.Contain("severity"));
    }

    [Test]
    public void Plan_EmptyBodyPart_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnPawnHediffs, "MissingBodyPart@")));
        Assert.That(error, Does.Contain("body part"));
    }

    [Test]
    public void Plan_EmptyHediffName_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnPawnHediffs, "@Leg:0.5")));
        Assert.That(error, Does.Contain("HediffDef"));
    }

    // A trailing separator (or an empty middle entry) is skipped, not treated as a nameless hediff.
    [Test]
    public void Plan_TrailingSemicolon_IsIgnored()
    {
        PawnPlan plan = Plan((StepArgs.SpawnPawnHediffs, "Flu:0.4; "));
        Assert.That(plan.Hediffs, Has.Count.EqualTo(1));
    }

    // ----- count and spacing ------------------------------------------------------------------

    [Test]
    public void Plan_CountAndSpacing_LaysARowAlongX()
    {
        PawnPlan plan = Plan(
            (StepArgs.SpawnPawnCount, "3"),
            (StepArgs.SpawnPawnSpacing, "2"));

        Assert.That(Offsets(plan), Is.EqualTo(new[] { "0,0", "2,0", "4,0" }));
    }

    [Test]
    public void Plan_DefaultSpacing_IsTwo()
    {
        PawnPlan plan = Plan((StepArgs.SpawnPawnCount, "2"));
        Assert.That(Offsets(plan), Is.EqualTo(new[] { "0,0", "2,0" }));
    }

    [Test]
    public void Plan_ZeroCount_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnPawnCount, "0")));
        Assert.That(error, Does.Contain(StepArgs.SpawnPawnCount));
    }

    [Test]
    public void Plan_CountOverCap_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnPawnCount, (PawnLayout.MaxCount + 1).ToString())));
        Assert.That(error, Does.Contain(StepArgs.SpawnPawnCount));
    }

    [Test]
    public void Plan_CountAtCap_IsAccepted()
    {
        PawnPlan plan = Plan((StepArgs.SpawnPawnCount, PawnLayout.MaxCount.ToString()));
        Assert.That(plan.Cells, Has.Count.EqualTo(PawnLayout.MaxCount));
    }

    [Test]
    public void Plan_NonNumericCount_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SpawnPawnCount, "lots")));
        Assert.That(error, Does.Contain(StepArgs.SpawnPawnCount));
    }

    // ----- anchor reuse -----------------------------------------------------------------------

    [Test]
    public void Plan_AbsoluteAnchorAndOffset_AreCarried()
    {
        PawnPlan plan = Plan(
            (StepArgs.SceneAnchor, "100,120"),
            (StepArgs.SceneOffset, "5,-3"));

        Assert.That(plan.Anchor, Is.EqualTo(SceneAnchorKind.Absolute));
        Assert.That(plan.AnchorX, Is.EqualTo(100));
        Assert.That(plan.AnchorZ, Is.EqualTo(120));
        Assert.That(plan.OffsetX, Is.EqualTo(5));
        Assert.That(plan.OffsetZ, Is.EqualTo(-3));
    }

    // ----- unknown args / flags ---------------------------------------------------------------

    [Test]
    public void Plan_UnknownArg_IsRejected()
    {
        string error = PlanError(KindArgs(("kindd", "Muffalo")));
        Assert.That(error, Does.Contain("kindd"));
    }

    [Test]
    public void Plan_UnfogFalse_IsCarried()
    {
        PawnPlan plan = Plan((StepArgs.SceneUnfog, "false"));
        Assert.That(plan.Unfog, Is.False);
    }

    [Test]
    public void Plan_ClearTrue_IsCarried()
    {
        PawnPlan plan = Plan((StepArgs.SceneClear, "true"));
        Assert.That(plan.Clear, Is.True);
    }

    [Test]
    public void Plan_NonBoolClear_IsRejected()
    {
        string error = PlanError(KindArgs((StepArgs.SceneClear, "yes")));
        Assert.That(error, Does.Contain(StepArgs.SceneClear));
    }
}
