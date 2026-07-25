using System.Globalization;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class SceneLayoutTests
{
    private static Dictionary<string, string> Args(params (string Key, string Value)[] args)
    {
        Dictionary<string, string> bag = new Dictionary<string, string>();
        foreach ((string key, string value) in args)
            bag[key] = value;
        return bag;
    }

    // Every PlaceThings plan needs a def, so the happy-path helpers supply one and the missing-def
    // case gets its own test.
    private static Dictionary<string, string> ThingArgs(params (string Key, string Value)[] args)
    {
        Dictionary<string, string> bag = Args(args);
        if (!bag.ContainsKey(StepArgs.SceneDef))
            bag[StepArgs.SceneDef] = "Wall";
        return bag;
    }

    private static ScenePlan Plan(params (string Key, string Value)[] args)
    {
        Assert.That(SceneLayout.TryPlan(ThingArgs(args), out ScenePlan plan, out string? error), Is.True,
            $"expected a valid plan, got error: {error}");
        return plan;
    }

    private static string PlanError(params (string Key, string Value)[] args)
    {
        Assert.That(SceneLayout.TryPlan(ThingArgs(args), out _, out string? error), Is.False,
            "expected the plan to be rejected");
        Assert.That(error, Is.Not.Null);
        return error!;
    }

    private static IEnumerable<string> Offsets(ScenePlan plan) =>
        plan.Cells.Select(c => $"{c.Dx},{c.Dz}");

    // ----- grid layout ------------------------------------------------------------------------

    // Grid is the default because it's the shadow-caster case, so bare args must produce a usable
    // lattice with no layout key at all.
    [Test]
    public void Plan_NoLayout_DefaultsToGrid()
    {
        ScenePlan plan = Plan();

        Assert.That(plan.Cells, Has.Count.EqualTo(SceneLayout.DefaultCols * SceneLayout.DefaultRows));
        Assert.That(plan.DefName, Is.EqualTo("Wall"));
        Assert.That(plan.Rotation, Is.EqualTo(SceneLayout.DefaultRotation));
        Assert.That(plan.Anchor, Is.EqualTo(SceneAnchorKind.MapCenter));
    }

    // An odd count divides evenly, so the lattice is exactly symmetric about the anchor.
    [Test]
    public void Plan_OddGrid_IsSymmetricAboutAnchor()
    {
        ScenePlan plan = Plan(
            (StepArgs.PlaceThingsCols, "3"),
            (StepArgs.PlaceThingsRows, "1"),
            (StepArgs.PlaceThingsSpacing, "4"));

        Assert.That(Offsets(plan), Is.EqualTo(new[] { "-4,0", "0,0", "4,0" }));
    }

    // An even count can't be centred on a discrete cell; the integer-divided half-span puts it one
    // cell off rather than on a fractional coordinate. Pinned because it's a deliberate choice.
    [Test]
    public void Plan_EvenGrid_LandsOneCellOffCentre()
    {
        ScenePlan plan = Plan(
            (StepArgs.PlaceThingsCols, "4"),
            (StepArgs.PlaceThingsRows, "1"),
            (StepArgs.PlaceThingsSpacing, "5"));

        Assert.That(Offsets(plan), Is.EqualTo(new[] { "-7,0", "-2,0", "3,0", "8,0" }));
    }

    [Test]
    public void Plan_GridSpacingOne_IsContiguous()
    {
        ScenePlan plan = Plan(
            (StepArgs.PlaceThingsCols, "2"),
            (StepArgs.PlaceThingsRows, "2"),
            (StepArgs.PlaceThingsSpacing, "1"));

        Assert.That(Offsets(plan), Is.EquivalentTo(new[] { "0,0", "0,1", "1,0", "1,1" }));
    }

    // ----- row layout ------------------------------------------------------------------------

    [TestCase(SceneLayout.AxisX, new[] { "-6,0", "-3,0", "0,0", "3,0", "6,0" })]
    [TestCase(SceneLayout.AxisZ, new[] { "0,-6", "0,-3", "0,0", "0,3", "0,6" })]
    public void Plan_Row_RunsAlongRequestedAxis(string axis, string[] expected)
    {
        ScenePlan plan = Plan(
            (StepArgs.PlaceThingsLayout, SceneLayout.LayoutRow),
            (StepArgs.PlaceThingsCount, "5"),
            (StepArgs.PlaceThingsSpacing, "3"),
            (StepArgs.PlaceThingsAxis, axis));

        Assert.That(Offsets(plan), Is.EqualTo(expected));
    }

    [Test]
    public void Plan_RowWithoutAxis_DefaultsToX()
    {
        ScenePlan plan = Plan(
            (StepArgs.PlaceThingsLayout, SceneLayout.LayoutRow),
            (StepArgs.PlaceThingsCount, "3"),
            (StepArgs.PlaceThingsSpacing, "2"));

        Assert.That(Offsets(plan), Is.EqualTo(new[] { "-2,0", "0,0", "2,0" }));
    }

    // ----- cells layout ----------------------------------------------------------------------

    [Test]
    public void Plan_ExplicitCells_ParsesNegativesAndWhitespace()
    {
        ScenePlan plan = Plan(
            (StepArgs.PlaceThingsLayout, SceneLayout.LayoutCells),
            (StepArgs.PlaceThingsCells, " 0,0 ;4,0;  8,-3 "));

        Assert.That(Offsets(plan), Is.EqualTo(new[] { "0,0", "4,0", "8,-3" }));
    }

    // A trailing separator on a list that obviously meant to parse shouldn't cost the author an
    // error.
    [Test]
    public void Plan_ExplicitCells_ToleratesTrailingSeparator()
    {
        ScenePlan plan = Plan(
            (StepArgs.PlaceThingsLayout, SceneLayout.LayoutCells),
            (StepArgs.PlaceThingsCells, "0,0; 2,2;"));

        Assert.That(Offsets(plan), Is.EqualTo(new[] { "0,0", "2,2" }));
    }

    [Test]
    public void Plan_CellsLayoutWithoutCellsArg_IsRejected()
    {
        string error = PlanError((StepArgs.PlaceThingsLayout, SceneLayout.LayoutCells));

        Assert.That(error, Does.Contain($"'{StepArgs.PlaceThingsCells}' is required"));
    }

    [TestCase("", "listed no cells")]
    [TestCase(";;", "listed no cells")]
    [TestCase("0,0,0", "two comma-separated whole numbers")]
    [TestCase("0", "two comma-separated whole numbers")]
    [TestCase("a,b", "two comma-separated whole numbers")]
    [TestCase("1.5,0", "two comma-separated whole numbers")]
    public void Plan_MalformedCells_IsRejected(string cells, string expectedFragment)
    {
        string error = PlanError(
            (StepArgs.PlaceThingsLayout, SceneLayout.LayoutCells),
            (StepArgs.PlaceThingsCells, cells));

        Assert.That(error, Does.Contain(expectedFragment));
    }

    // Two things on one cell means WipeMode.Vanish destroys the first to place the second, so the
    // scenario would spawn fewer casters than it reads as spawning.
    [Test]
    public void Plan_DuplicateCells_IsRejected()
    {
        string error = PlanError(
            (StepArgs.PlaceThingsLayout, SceneLayout.LayoutCells),
            (StepArgs.PlaceThingsCells, "3,4; 0,0; 3,4"));

        Assert.That(error, Does.Contain("duplicate cell offset (3,4)"));
    }

    // ----- arg whitelisting -------------------------------------------------------------------

    [Test]
    public void Plan_UnknownArg_IsRejected()
    {
        string error = PlanError(("spaceing", "5"));

        Assert.That(error, Does.Contain("unknown arg 'spaceing'"));
    }

    // The whitelist is per-layout, not a union: an arg that looks honoured but isn't would produce a
    // plausible-looking wrong scene. Each case supplies only the foreign arg on top of an otherwise
    // valid set, so the reported key is unambiguous — with two unknown args the message would name
    // whichever the dictionary happened to yield first.
    [TestCase(SceneLayout.LayoutRow, "cols")]
    [TestCase(SceneLayout.LayoutRow, "rows")]
    [TestCase(SceneLayout.LayoutRow, "cells")]
    [TestCase(SceneLayout.LayoutGrid, "count")]
    [TestCase(SceneLayout.LayoutGrid, "axis")]
    [TestCase(SceneLayout.LayoutGrid, "cells")]
    public void Plan_ArgFromAnotherLayout_IsRejected(string layout, string foreignArg)
    {
        string error = PlanError(
            (StepArgs.PlaceThingsLayout, layout),
            (foreignArg, "2"));

        Assert.That(error, Does.Contain($"unknown arg '{foreignArg}'"));
    }

    [TestCase("spacing")]
    [TestCase("cols")]
    [TestCase("count")]
    public void Plan_CellsLayout_RejectsParametricArgs(string foreignArg)
    {
        string error = PlanError(
            (StepArgs.PlaceThingsLayout, SceneLayout.LayoutCells),
            (StepArgs.PlaceThingsCells, "0,0"),
            (foreignArg, "2"));

        Assert.That(error, Does.Contain($"unknown arg '{foreignArg}'"));
    }

    [Test]
    public void Plan_UnknownLayout_IsRejected()
    {
        string error = PlanError((StepArgs.PlaceThingsLayout, "diagonal"));

        Assert.That(error, Does.Contain($"'{StepArgs.PlaceThingsLayout}' must be one of"));
    }

    // ----- def, stuff, rotation ---------------------------------------------------------------

    [TestCase("")]
    [TestCase("   ")]
    public void Plan_MissingOrBlankDef_IsRejected(string def)
    {
        Assert.That(
            SceneLayout.TryPlan(Args((StepArgs.SceneDef, def)), out _, out string? error),
            Is.False);
        Assert.That(error, Does.Contain($"'{StepArgs.SceneDef}' is required"));
    }

    [Test]
    public void Plan_NoDefArgAtAll_IsRejected()
    {
        Assert.That(SceneLayout.TryPlan(Args(), out _, out string? error), Is.False);
        Assert.That(error, Does.Contain($"'{StepArgs.SceneDef}' is required"));
    }

    // Absent stuff is not the same as empty stuff: absent means "let the adapter pick the def's
    // default", which is why it stays null rather than "".
    [Test]
    public void Plan_NoStuff_LeavesStuffNull()
    {
        Assert.That(Plan().StuffDefName, Is.Null);
    }

    [Test]
    public void Plan_ExplicitStuff_IsCarried()
    {
        Assert.That(Plan((StepArgs.PlaceThingsStuff, "BlocksGranite")).StuffDefName,
            Is.EqualTo("BlocksGranite"));
    }

    [Test]
    public void Plan_EmptyStuff_IsRejected()
    {
        Assert.That(PlanError((StepArgs.PlaceThingsStuff, "")),
            Does.Contain($"'{StepArgs.PlaceThingsStuff}' must not be empty"));
    }

    [TestCase("North")]
    [TestCase("East")]
    [TestCase("South")]
    [TestCase("West")]
    public void Plan_ValidRotation_IsCarried(string rot)
    {
        Assert.That(Plan((StepArgs.PlaceThingsRot, rot)).Rotation, Is.EqualTo(rot));
    }

    // Rot4.FromString silently yields North for anything it doesn't recognise, so a lowercase or
    // misspelled rotation has to be caught here or it would rotate nothing and say nothing.
    [TestCase("north")]
    [TestCase("Noth")]
    [TestCase("0")]
    public void Plan_InvalidRotation_IsRejected(string rot)
    {
        Assert.That(PlanError((StepArgs.PlaceThingsRot, rot)),
            Does.Contain($"'{StepArgs.PlaceThingsRot}' must be one of"));
    }

    // ----- unfog ------------------------------------------------------------------------------

    // Defaults ON: a scene built at map centre on a freshly generated colony is invisible under fog
    // while every step still reports success, which is a green run over a blank screenshot.
    [Test]
    public void Plan_UnfogDefaultsOn()
    {
        Assert.That(Plan().Unfog, Is.True);
        Assert.That(SceneLayout.DefaultUnfog, Is.True);
    }

    [TestCase("true", true)]
    [TestCase("false", false)]
    [TestCase("True", true)]
    [TestCase("FALSE", false)]
    public void Plan_UnfogIsCarried(string raw, bool expected)
    {
        Assert.That(Plan((StepArgs.SceneUnfog, raw)).Unfog, Is.EqualTo(expected));
    }

    // Only true/false, so a typo can't quietly mean false.
    [TestCase("1")]
    [TestCase("yes")]
    [TestCase("ture")]
    public void Plan_NonBooleanUnfog_IsRejected(string raw)
    {
        Assert.That(PlanError((StepArgs.SceneUnfog, raw)),
            Does.Contain($"'{StepArgs.SceneUnfog}' must be true or false"));
    }

    [Test]
    public void PlanTerrain_UnfogDefaultsOnAndIsCarried()
    {
        Assert.That(
            SceneLayout.TryPlanTerrain(Args((StepArgs.SceneDef, "Concrete")), out TerrainPlan on, out _),
            Is.True);
        Assert.That(on.Unfog, Is.True);

        Assert.That(
            SceneLayout.TryPlanTerrain(
                Args((StepArgs.SceneDef, "Concrete"), (StepArgs.SceneUnfog, "false")),
                out TerrainPlan off, out _),
            Is.True);
        Assert.That(off.Unfog, Is.False);
    }

    // LookAt builds nothing, so it has no fog to lift — accepting the arg would imply otherwise.
    [Test]
    public void PlanLookAt_UnfogArg_IsRejected()
    {
        Assert.That(
            SceneLayout.TryPlanLookAt(Args((StepArgs.SceneUnfog, "true")), out _, out string? error),
            Is.False);
        Assert.That(error, Does.Contain($"unknown arg '{StepArgs.SceneUnfog}'"));
    }

    // ----- anchor and offset ------------------------------------------------------------------

    [Test]
    public void Plan_AbsoluteAnchor_IsCarried()
    {
        ScenePlan plan = Plan((StepArgs.SceneAnchor, "125,200"));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Anchor, Is.EqualTo(SceneAnchorKind.Absolute));
            Assert.That(plan.AnchorX, Is.EqualTo(125));
            Assert.That(plan.AnchorZ, Is.EqualTo(200));
        });
    }

    [Test]
    public void Plan_Offset_IsCarriedAndDefaultsToZero()
    {
        ScenePlan offset = Plan((StepArgs.SceneOffset, "-10,4"));
        Assert.Multiple(() =>
        {
            Assert.That(offset.OffsetX, Is.EqualTo(-10));
            Assert.That(offset.OffsetZ, Is.EqualTo(4));
        });

        ScenePlan none = Plan();
        Assert.Multiple(() =>
        {
            Assert.That(none.OffsetX, Is.Zero);
            Assert.That(none.OffsetZ, Is.Zero);
        });
    }

    // Map cells are never negative, so a negative anchor is a mistake rather than a coordinate; the
    // offset arg is the way to move away from an anchor.
    [TestCase("-1,5")]
    [TestCase("5,-1")]
    public void Plan_NegativeAbsoluteAnchor_IsRejected(string anchor)
    {
        Assert.That(PlanError((StepArgs.SceneAnchor, anchor)), Does.Contain("non-negative map"));
    }

    [TestCase("middle")]
    [TestCase("125")]
    [TestCase("125,125,0")]
    public void Plan_MalformedAnchor_IsRejected(string anchor)
    {
        Assert.That(PlanError((StepArgs.SceneAnchor, anchor)),
            Does.Contain($"'{StepArgs.SceneAnchor}'"));
    }

    // ----- numeric guards ---------------------------------------------------------------------

    [TestCase(StepArgs.PlaceThingsCols, "0")]
    [TestCase(StepArgs.PlaceThingsCols, "-1")]
    [TestCase(StepArgs.PlaceThingsRows, "0")]
    [TestCase(StepArgs.PlaceThingsSpacing, "0")]
    public void Plan_NonPositiveGridArg_IsRejected(string key, string value)
    {
        Assert.That(PlanError((key, value)), Does.Contain("must be at least 1"));
    }

    [TestCase(StepArgs.PlaceThingsCols, "abc")]
    [TestCase(StepArgs.PlaceThingsSpacing, "1.5")]
    public void Plan_NonIntegerGridArg_IsRejected(string key, string value)
    {
        Assert.That(PlanError((key, value)), Does.Contain("is not a whole number"));
    }

    // Guards a fat-fingered cols/rows from a several-thousand-thing spawn that would tank the
    // framerate the screenshots depend on. Referenced from the const, not a literal.
    [Test]
    public void Plan_AtPlacementCap_IsAccepted()
    {
        ScenePlan plan = Plan(
            (StepArgs.PlaceThingsCols, "16"),
            (StepArgs.PlaceThingsRows, (SceneLayout.MaxPlacements / 16).ToString()),
            (StepArgs.PlaceThingsSpacing, "1"));

        Assert.That(plan.Cells, Has.Count.EqualTo(SceneLayout.MaxPlacements));
    }

    [Test]
    public void Plan_OverPlacementCap_IsRejected()
    {
        string error = PlanError(
            (StepArgs.PlaceThingsCols, "16"),
            (StepArgs.PlaceThingsRows, (SceneLayout.MaxPlacements / 16 + 1).ToString()),
            (StepArgs.PlaceThingsSpacing, "1"));

        Assert.That(error, Does.Contain($"exceeds the {SceneLayout.MaxPlacements}-placement cap"));
    }

    // 65536 * 65536 overflows an int product to exactly 0, which would sail past a naive cap check and
    // leave the caller looping ~4 billion times. Both caps multiply as long; these pin that.
    [Test]
    public void Plan_GridDimensionsThatOverflowIntProduct_AreStillRejected()
    {
        string error = PlanError(
            (StepArgs.PlaceThingsCols, "65536"),
            (StepArgs.PlaceThingsRows, "65536"),
            (StepArgs.PlaceThingsSpacing, "1"));

        Assert.That(error, Does.Contain($"exceeds the {SceneLayout.MaxPlacements}-placement cap"));
    }

    // ----- SetTerrain -------------------------------------------------------------------------

    [Test]
    public void PlanTerrain_Defaults_AreASquareAtMapCentre()
    {
        Assert.That(
            SceneLayout.TryPlanTerrain(Args((StepArgs.SceneDef, "Concrete")), out TerrainPlan plan, out string? error),
            Is.True, $"expected a valid plan, got error: {error}");

        Assert.Multiple(() =>
        {
            Assert.That(plan.DefName, Is.EqualTo("Concrete"));
            Assert.That(plan.Width, Is.EqualTo(SceneLayout.DefaultTerrainWidth));
            Assert.That(plan.Height, Is.EqualTo(SceneLayout.DefaultTerrainHeight));
            Assert.That(plan.Anchor, Is.EqualTo(SceneAnchorKind.MapCenter));
        });
    }

    [Test]
    public void PlanTerrain_MissingDef_IsRejected()
    {
        Assert.That(SceneLayout.TryPlanTerrain(Args(), out _, out string? error), Is.False);
        Assert.That(error, Does.Contain($"'{StepArgs.SceneDef}' is required"));
    }

    // PlaceThings-only args must not be silently accepted here either.
    [TestCase("stuff")]
    [TestCase("layout")]
    [TestCase("spacing")]
    public void PlanTerrain_PlaceThingsArg_IsRejected(string foreignArg)
    {
        Assert.That(
            SceneLayout.TryPlanTerrain(
                Args((StepArgs.SceneDef, "Concrete"), (foreignArg, "1")), out _, out string? error),
            Is.False);
        Assert.That(error, Does.Contain($"unknown arg '{foreignArg}'"));
    }

    [TestCase(StepArgs.SetTerrainWidth, "0")]
    [TestCase(StepArgs.SetTerrainHeight, "-4")]
    public void PlanTerrain_NonPositiveExtent_IsRejected(string key, string value)
    {
        Assert.That(
            SceneLayout.TryPlanTerrain(
                Args((StepArgs.SceneDef, "Concrete"), (key, value)), out _, out string? error),
            Is.False);
        Assert.That(error, Does.Contain("must be at least 1"));
    }

    [Test]
    public void PlanTerrain_OverCellCap_IsRejected()
    {
        int side = (int)Math.Sqrt(SceneLayout.MaxTerrainCells) + 1;

        Assert.That(
            SceneLayout.TryPlanTerrain(
                Args(
                    (StepArgs.SceneDef, "Concrete"),
                    (StepArgs.SetTerrainWidth, side.ToString()),
                    (StepArgs.SetTerrainHeight, side.ToString())),
                out _, out string? error),
            Is.False);
        Assert.That(error, Does.Contain($"exceeds the {SceneLayout.MaxTerrainCells}-cell terrain cap"));
    }

    [Test]
    public void PlanTerrain_ExtentsThatOverflowIntProduct_AreStillRejected()
    {
        Assert.That(
            SceneLayout.TryPlanTerrain(
                Args(
                    (StepArgs.SceneDef, "Concrete"),
                    (StepArgs.SetTerrainWidth, "65536"),
                    (StepArgs.SetTerrainHeight, "65536")),
                out _, out string? error),
            Is.False);
        Assert.That(error, Does.Contain($"exceeds the {SceneLayout.MaxTerrainCells}-cell terrain cap"));
    }

    // ----- LookAt -----------------------------------------------------------------------------

    [Test]
    public void PlanLookAt_NoArgs_IsMapCentreAtCurrentZoom()
    {
        Assert.That(SceneLayout.TryPlanLookAt(Args(), out LookAtPlan plan, out string? error), Is.True,
            $"expected a valid plan, got error: {error}");

        Assert.Multiple(() =>
        {
            Assert.That(plan.Anchor, Is.EqualTo(SceneAnchorKind.MapCenter));
            Assert.That(plan.Zoom, Is.Null);
        });
    }

    [Test]
    public void PlanLookAt_Zoom_IsCarried()
    {
        Assert.That(SceneLayout.TryPlanLookAt(Args((StepArgs.LookAtZoom, "28.5")), out LookAtPlan plan, out _), Is.True);
        Assert.That(plan.Zoom, Is.EqualTo(28.5));
    }

    [TestCase("0", "must be greater than 0")]
    [TestCase("-5", "must be greater than 0")]
    [TestCase("wide", "is not a number")]
    public void PlanLookAt_InvalidZoom_IsRejected(string zoom, string expectedFragment)
    {
        Assert.That(SceneLayout.TryPlanLookAt(Args((StepArgs.LookAtZoom, zoom)), out _, out string? error), Is.False);
        Assert.That(error, Does.Contain(expectedFragment));
    }

    // A camera move has nothing to look *with*, so accepting `def` would suggest it did.
    [Test]
    public void PlanLookAt_DefArg_IsRejected()
    {
        Assert.That(SceneLayout.TryPlanLookAt(Args((StepArgs.SceneDef, "Wall")), out _, out string? error), Is.False);
        Assert.That(error, Does.Contain($"unknown arg '{StepArgs.SceneDef}'"));
    }

    // ----- culture independence ---------------------------------------------------------------

    // A scenario file is data that has to read the same on any machine. Under a comma-decimal locale
    // a naive parse would read "28.5" as 285 and "1,5" as 1.5, silently shifting the scene rather
    // than failing. Pinned by running both under a comma-decimal culture.
    [Test]
    public void Plan_ParsesInvariantlyUnderCommaDecimalCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            Assert.That(
                SceneLayout.TryPlanLookAt(Args((StepArgs.LookAtZoom, "28.5")), out LookAtPlan plan, out _),
                Is.True);
            Assert.That(plan.Zoom, Is.EqualTo(28.5));

            // "1,5" is a decimal in de-DE but must not be accepted as one here.
            Assert.That(SceneLayout.TryPlanLookAt(Args((StepArgs.LookAtZoom, "1,5")), out _, out string? error), Is.False);
            Assert.That(error, Does.Contain("is not a number"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
