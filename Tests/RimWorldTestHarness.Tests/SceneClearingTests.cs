using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class SceneClearingTests
{
    private static ClearTally Tally(int cells, int roofed, int destroyed) =>
        new ClearTally { Cells = cells, RoofedCells = roofed, ThingsDestroyed = destroyed };

    // ----- what may be destroyed --------------------------------------------------------------

    // The four categories that physically sit on the ground a lighting screenshot is of. Referenced
    // through the consts, not literals, so the test and the whitelist can't drift.
    [TestCase(SceneClearing.CategoryBuilding)]
    [TestCase(SceneClearing.CategoryPlant)]
    [TestCase(SceneClearing.CategoryItem)]
    [TestCase(SceneClearing.CategoryFilth)]
    public void Classify_DestroyableGroundThing_IsDestroyed(string category)
    {
        Assert.That(SceneClearing.Classify(category, destroyable: true, out string? reason),
            Is.EqualTo(ClearVerdict.Destroy));
        Assert.That(reason, Is.Null, "a destroy verdict has nothing to explain");
    }

    // The case that motivated the whole issue: a mineable rock wall is a destroyable Building, and
    // GenSpawn.CanSpawnAt refuses a placement on it anyway (it isn't walkable) — so clearing has to be
    // willing to destroy it or PlaceThings can never recover the cell.
    [Test]
    public void Classify_MineableRockWall_IsDestroyed()
    {
        Assert.That(
            SceneClearing.Classify(SceneClearing.CategoryBuilding, destroyable: true, out _),
            Is.EqualTo(ClearVerdict.Destroy));
    }

    // A pawn can wander onto the pad between a scenario being written and the step running. Destroying
    // one would make runs both destructive and nondeterministic, and a pawn is passable so it never
    // refuses a placement anyway.
    [Test]
    public void Classify_Pawn_IsLeftAloneWithItsOwnReason()
    {
        Assert.That(
            SceneClearing.Classify(SceneClearing.CategoryPawn, destroyable: true, out string? reason),
            Is.EqualTo(ClearVerdict.Leave));
        Assert.That(reason, Is.EqualTo(SceneClearing.ReasonPawn));
    }

    // Whitelist, not blacklist: transient decoration neither blocks a build nor shades a cell, and a
    // category RimWorld adds in a future version must fail in the same safe direction rather than
    // being silently bulldozed.
    [TestCase("Projectile")]
    [TestCase("Mote")]
    [TestCase("Gas")]
    [TestCase("Attachment")]
    [TestCase("Ethereal")]
    [TestCase("PsychicEmitter")]
    [TestCase("None")]
    [TestCase("SomethingLudeonAddsIn17")]
    public void Classify_UnlistedCategory_IsLeftAlone(string category)
    {
        Assert.That(SceneClearing.Classify(category, destroyable: true, out string? reason),
            Is.EqualTo(ClearVerdict.Leave));
        Assert.That(reason, Does.Contain(category),
            "the reason has to name the category, or a spared thing is a mystery in the log");
    }

    // Checked before calling Destroy, because Thing.Destroy Log.Errors on a non-destroyable thing and
    // returns — which would bury the diagnosis in Player.log instead of the run's report.
    [TestCase(SceneClearing.CategoryBuilding)]
    [TestCase(SceneClearing.CategoryItem)]
    public void Classify_NonDestroyableGroundThing_IsBlocked(string category)
    {
        Assert.That(SceneClearing.Classify(category, destroyable: false, out string? reason),
            Is.EqualTo(ClearVerdict.Blocked));
        Assert.That(reason, Does.Contain("not destroyable"));
    }

    // Category is checked first: a non-destroyable pawn (or mote) is spared, not reported as a blocker,
    // because clearing was never going to remove it in the first place.
    [Test]
    public void Classify_NonDestroyablePawn_IsLeftAloneNotBlocked()
    {
        Assert.That(
            SceneClearing.Classify(SceneClearing.CategoryPawn, destroyable: false, out _),
            Is.EqualTo(ClearVerdict.Leave));
    }

    // Categories arrive as the vanilla enum's member name, which is case-sensitive. A near-miss must
    // fall through to Leave rather than being treated as clearable — the safe direction.
    [TestCase("building")]
    [TestCase("BUILDING")]
    [TestCase("")]
    public void Classify_CategoryMatchingIsExact(string category)
    {
        Assert.That(SceneClearing.Classify(category, destroyable: true, out _),
            Is.EqualTo(ClearVerdict.Leave));
    }

    // ----- the gate ---------------------------------------------------------------------------

    // Nothing to say means say nothing: a clean clear must not manufacture a step error.
    [Test]
    public void BlockedError_NoBlockers_IsNull()
    {
        Assert.That(SceneClearing.BlockedError(Tally(16, 0, 4)), Is.Null);
    }

    // The core requirement of the issue: clearing that couldn't remove something has to be reported,
    // never absorbed. A green run over a half-buried pad is the failure class this harness exists for.
    [Test]
    public void BlockedError_NamesCountAndEveryBlocker()
    {
        ClearTally tally = Tally(4, 0, 1);
        tally.Blocked.Add("(128,118) Wall — its def is flagged not destroyable");
        tally.Blocked.Add("(133,123) SteamGeyser — its def is flagged not destroyable");

        string? error = SceneClearing.BlockedError(tally);

        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("2 blocker"));
        Assert.That(error, Does.Contain("(128,118) Wall"));
        Assert.That(error, Does.Contain("(133,123) SteamGeyser"));
    }

    // Spared things (pawns, motes) are logged, never gated on — otherwise a colonist standing on the
    // pad would turn a correct scene into a red run.
    [Test]
    public void BlockedError_IgnoresSparedThings()
    {
        ClearTally tally = Tally(4, 0, 0);
        tally.Left.Add($"(128,118) Human — {SceneClearing.ReasonPawn}");

        Assert.That(SceneClearing.BlockedError(tally), Is.Null);
    }

    // ----- capped reporting -------------------------------------------------------------------

    [Test]
    public void FormatList_UnderCap_ListsEverythingWithNoElision()
    {
        string[] items = { "a", "b", "c" };

        string formatted = SceneClearing.FormatList(items);

        Assert.That(formatted, Is.EqualTo("a, b, c"));
        Assert.That(formatted, Does.Not.Contain("more"));
    }

    [Test]
    public void FormatList_Empty_IsEmpty()
    {
        Assert.That(SceneClearing.FormatList(new string[0]), Is.Empty);
    }

    [Test]
    public void FormatList_AtCap_ListsEverythingWithNoElision()
    {
        string[] items = Enumerable.Range(0, SceneClearing.MaxReportedThings)
            .Select(i => $"item{i}")
            .ToArray();

        Assert.That(SceneClearing.FormatList(items), Does.Not.Contain("more"));
    }

    // SetTerrain's footprint can be 16384 cells, so an uncapped list would bury the run's actual
    // verdict. The count is still stated in full by the caller, and entries are elided whole rather
    // than truncated mid-entry.
    [Test]
    public void FormatList_OverCap_ElidesTheTailAndSaysHowMany()
    {
        int over = SceneClearing.MaxReportedThings + 5;
        string[] items = Enumerable.Range(0, over).Select(i => $"item{i}").ToArray();

        string formatted = SceneClearing.FormatList(items);

        Assert.That(formatted, Does.Contain($"item{SceneClearing.MaxReportedThings - 1}"));
        Assert.That(formatted, Does.Not.Contain($"item{SceneClearing.MaxReportedThings}"));
        Assert.That(formatted, Does.Contain("and 5 more"));
    }

    // ----- wording ----------------------------------------------------------------------------

    // Counts, not prose: when a screenshot looks wrong the question being asked of Player.log is "did
    // it strip the roof or not".
    [Test]
    public void Describe_StatesCellsThingsAndRoofs()
    {
        string described = SceneClearing.Describe(Tally(cells: 1600, roofed: 240, destroyed: 37));

        Assert.That(described, Does.Contain("1600"));
        Assert.That(described, Does.Contain("37"));
        Assert.That(described, Does.Contain("240"));
    }

    [Test]
    public void RefusalDetail_NoBlockers_AddsNothing()
    {
        Assert.That(SceneClearing.RefusalDetail(new string[0]), Is.Empty);
    }

    // Appended to a refused cell so the report names what is still standing there instead of leaving
    // the author to guess whether it was rock, water or a wall they forgot about.
    [Test]
    public void RefusalDetail_WithBlockers_NamesThem()
    {
        string detail = SceneClearing.RefusalDetail(new[] { "SteamGeyser (its def is flagged not destroyable)" });

        Assert.That(detail, Does.Contain("uncleared"));
        Assert.That(detail, Does.Contain("SteamGeyser"));
    }
}
