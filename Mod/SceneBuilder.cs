using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod;

// The live-game half of scene setup: resolves a pure SceneLayout plan against a real Map and calls
// vanilla's own spawn/terrain/camera APIs. All of the branching and arithmetic lives in
// Shared/SceneLayout.cs; this file only does what can't be done without Verse types.
//
// Following the convention HarnessDebugActions states — make it a real [DebugAction] with a shared
// core, don't bolt on a bespoke path — the same cores below back both the scenario steps and a
// dev-menu entry, so a human poking at a running game and an unattended scenario run exercise
// identical code.
public static class SceneBuilder
{
    // ----- anchor resolution ------------------------------------------------------------------

    // A plan's coordinates are anchor-relative (that's what keeps SceneLayout pure and map-size
    // independent), so this is where they become real cells. Defaulting to map centre is what lets
    // one scenario work against both the committed fixture and the -quicktest throwaway colony.
    private static IntVec3 ResolveAnchor(IAnchoredPlan plan, Map map)
    {
        IntVec3 origin = plan.Anchor == SceneAnchorKind.MapCenter
            ? map.Center
            : new IntVec3(plan.AnchorX, 0, plan.AnchorZ);

        return origin + new IntVec3(plan.OffsetX, 0, plan.OffsetZ);
    }

    // ----- PlaceThings ------------------------------------------------------------------------

    // Spawns the plan's cells, returning an error describing any shortfall. Counting what actually
    // landed is the whole point: GenSpawn.Spawn returns null when CanSpawnAt refuses a cell (water,
    // steep terrain, an existing edifice it won't wipe), and a scenario that silently placed 4 of 16
    // pillars would produce a screenshot that looks deliberate and is wrong.
    public static string? Build(Map map, ScenePlan plan)
    {
        if (!TryResolveThingDef(plan.DefName, out ThingDef def, out string? error))
            return error;
        if (!TryResolveStuff(def, plan.StuffDefName, out ThingDef? stuff, out error))
            return error;

        IntVec3 anchor = ResolveAnchor(plan, map);
        Rot4 rot = Rot4.FromString(plan.Rotation);

        List<string> refused = new List<string>();
        int placed = 0;

        foreach (ScenePlacement placement in plan.Cells)
        {
            IntVec3 cell = anchor + new IntVec3(placement.Dx, 0, placement.Dz);
            if (TrySpawnOne(def, stuff, cell, rot, map, out string? reason))
                placed++;
            else
                refused.Add($"({cell.x},{cell.z}) {reason}");
        }

        if (plan.Unfog)
            Unfog(map);

        if (refused.Count == 0)
            return null;

        // Every refused cell is listed rather than just counted, because "which cells" is what tells
        // a scenario author whether their anchor is wrong or the terrain is.
        return $"placed {placed} of {plan.Cells.Count} {def.defName} — " +
               $"{refused.Count} refused: {string.Join(", ", refused)}";
    }

    private static bool TrySpawnOne(
        ThingDef def, ThingDef? stuff, IntVec3 cell, Rot4 rot, Map map, out string? reason)
    {
        if (!cell.InBounds(map))
        {
            reason = "out of bounds";
            return false;
        }

        // CanSpawnAt is checked explicitly rather than inferred from the spawn call, because the
        // Thing overload of GenSpawn.Spawn does NOT consult it — unlike the ThingDef overload, it
        // returns null only for a null map, an out-of-bounds cell (which the guard above already
        // covers) or an already-spawned thing. Without this, a wall asked to stand in deep water
        // would be reported as placed. CanSpawnAt covers terrain buildability, walkability and
        // indestructible blockers; canWipeEdifices defaults to true, matching WipeMode.Vanish below.
        if (!GenSpawn.CanSpawnAt(def, cell, map, rot))
        {
            reason = "terrain or an indestructible blocker refuses it";
            return false;
        }

        // Stuff is resolved up front rather than left to ThingMaker, whose MadeFromStuff mismatch
        // path logs a Log.Error and silently substitutes a default — an error in the player log that
        // never reaches the run's report.
        Thing thing = ThingMaker.MakeThing(def, stuff);

        // WipeMode.Vanish (vanilla's default) destroys whatever occupies the cell. Acceptable here
        // because batch runs never save the game — the fixture is restored by run_test.sh — and it's
        // why these steps are deliberately kept off the live channel, which points at a real
        // player's colony.
        if (GenSpawn.Spawn(thing, cell, map, rot, WipeMode.Vanish) == null)
        {
            reason = "spawn refused it";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool TryResolveThingDef(string defName, out ThingDef def, out string? error)
    {
        // errorOnFail: false — an unknown def is a scenario bug that belongs in the report, not a
        // Log.Error buried in Player.log.
        def = DefDatabase<ThingDef>.GetNamed(defName, errorOnFail: false);
        if (def == null)
        {
            error = $"no ThingDef named '{defName}' in the active modset";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryResolveStuff(
        ThingDef def, string? stuffDefName, out ThingDef? stuff, out string? error)
    {
        stuff = null;

        if (stuffDefName != null)
        {
            stuff = DefDatabase<ThingDef>.GetNamed(stuffDefName, errorOnFail: false);
            if (stuff == null)
            {
                error = $"no ThingDef named '{stuffDefName}' in the active modset";
                return false;
            }

            if (!stuff.IsStuff)
            {
                error = $"'{stuffDefName}' is not a stuff def — it can't be used as material for " +
                        $"'{def.defName}'";
                return false;
            }

            if (!def.MadeFromStuff)
            {
                error = $"'{def.defName}' is not made from stuff, so '{stuffDefName}' can't apply";
                return false;
            }

            error = null;
            return true;
        }

        if (def.MadeFromStuff)
            stuff = GenStuff.DefaultStuffFor(def);

        error = null;
        return true;
    }

    // ----- fog --------------------------------------------------------------------------------

    // Lifts fog across the whole map, not just the built footprint. Deliberate: a shadow falls well
    // outside the cells its caster occupies, and at a low sun it falls a long way, so unfogging only
    // the footprint would still hide most of what the scene exists to show — and picking a margin
    // large enough would amount to the whole map anyway. Nothing here is ever saved (batch runs
    // restore the fixture and quit), so there's no lasting effect on a colony.
    //
    // Without this a scene built at map centre on a freshly generated colony is completely invisible:
    // RimWorld draws neither terrain nor things in fogged cells, while every step still reports
    // success — a green run over a blank screenshot.
    private static void Unfog(Map map)
    {
        map.fogGrid.ClearAllFog();
    }

    // ----- SetTerrain -------------------------------------------------------------------------

    // Uniform ground under the casters so shadow contrast reads consistently rather than fighting
    // whatever biome texture the fixture happens to have.
    public static string? PaintTerrain(Map map, TerrainPlan plan)
    {
        TerrainDef def = DefDatabase<TerrainDef>.GetNamed(plan.DefName, errorOnFail: false);
        if (def == null)
            return $"no TerrainDef named '{plan.DefName}' in the active modset";

        IntVec3 anchor = ResolveAnchor(plan, map);
        CellRect rect = CellRect.CenteredOn(anchor, plan.Width, plan.Height);

        int painted = 0;
        int outOfBounds = 0;

        foreach (IntVec3 cell in rect)
        {
            if (cell.InBounds(map))
            {
                map.terrainGrid.SetTerrain(cell, def);
                painted++;
            }
            else
            {
                outOfBounds++;
            }
        }

        if (plan.Unfog)
            Unfog(map);

        if (painted == 0)
            return $"painted no cells — the {plan.Width}x{plan.Height} rect at " +
                   $"({anchor.x},{anchor.z}) lies entirely outside the map";

        // A partly-clipped rect is reported but not treated as failure: it still produced usable
        // ground, and the count says exactly how much.
        if (outOfBounds > 0)
            Log.Message($"RWTH: SetTerrain painted {painted} cells, {outOfBounds} were out of bounds");

        return null;
    }

    // ----- LookAt -----------------------------------------------------------------------------

    // JumpToCurrentMapLoc rather than PanToMapLoc: the pan animates over several frames, which a
    // scenario would then have to wait out before screenshotting. The jump is instant.
    public static string? LookAt(Map map, LookAtPlan plan)
    {
        IntVec3 cell = ResolveAnchor(plan, map);
        if (!cell.InBounds(map))
            return $"({cell.x},{cell.z}) is outside the {map.Size.x}x{map.Size.z} map";

        Find.CameraDriver.JumpToCurrentMapLoc(cell);

        if (plan.Zoom is double zoom)
            Find.CameraDriver.SetRootSize((float)zoom);

        return null;
    }

    // ----- dev-menu entry ---------------------------------------------------------------------

    // The shadow-caster case as a one-click dev action, so the scene can be set up by hand in a
    // normally-launched game — useful when eyeballing a lighting change interactively, and the
    // reason these cores aren't private to StepExecutor.
    [DebugAction("RimWorldTestHarness", "Place shadow-caster grid",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void DevActionPlaceShadowCasters()
    {
        Map map = Find.CurrentMap;

        // Routed through the same pure planner the steps use, rather than hand-building cells, so the
        // dev action can't drift from what a scenario would produce.
        Dictionary<string, string> args = new Dictionary<string, string>
        {
            { StepArgs.SceneDef, "Wall" },
            { StepArgs.PlaceThingsLayout, SceneLayout.LayoutGrid },
        };

        if (!SceneLayout.TryPlan(args, out ScenePlan plan, out string? error))
        {
            Messages.Message($"RWTH scene plan failed: {error}", MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        string? buildError = Build(map, plan);
        Messages.Message(
            buildError ?? $"RWTH placed {plan.Cells.Count} shadow casters at map centre",
            buildError == null ? MessageTypeDefOf.TaskCompletion : MessageTypeDefOf.RejectInput,
            historical: false);
    }
}
