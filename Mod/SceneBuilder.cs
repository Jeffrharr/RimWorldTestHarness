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
        ClearTally tally = new ClearTally();
        List<string> blockersHere = new List<string>();
        int placed = 0;

        foreach (ScenePlacement placement in plan.Cells)
        {
            IntVec3 cell = anchor + new IntVec3(placement.Dx, 0, placement.Dz);

            // Reused across cells rather than allocated per cell: a 512-placement grid would otherwise
            // churn 512 lists for a case that is almost always empty.
            blockersHere.Clear();

            // Clearing runs BEFORE TrySpawnOne, and TrySpawnOne's CanSpawnAt check is then re-run
            // rather than skipped. That ordering is the whole point: CanSpawnAt fails on
            // !c.Walkable(map), so a *mineable* — and therefore destroyable — rock wall refuses a
            // placement even though clearing can remove it. Skipping the re-check instead would turn
            // genuinely impossible cells (deep water, an indestructible edifice) into silent successes.
            PrepareCell(map, cell, plan.Clear, tally, blockersHere);

            if (TrySpawnOne(def, stuff, cell, rot, map, out string? reason))
                placed++;
            else
                refused.Add($"({cell.x},{cell.z}) {reason}{SceneClearing.RefusalDetail(blockersHere)}");
        }

        if (plan.Unfog)
            Unfog(map);

        return JoinProblems(
            ShortfallError(placed, plan.Cells.Count, def.defName, refused),
            ReportClearing(StepArgs.PlaceThingsType, plan.Clear, tally));
    }

    // Every refused cell is listed rather than just counted, because "which cells" is what tells a
    // scenario author whether their anchor is wrong or the terrain is.
    private static string? ShortfallError(int placed, int wanted, string defName, List<string> refused)
    {
        if (refused.Count == 0)
            return null;

        return $"placed {placed} of {wanted} {defName} — " +
               $"{refused.Count} refused: {string.Join(", ", refused)}";
    }

    // A step can fail in two independent ways once clearing exists — cells refused a thing, and
    // clearing couldn't remove a blocker — and reporting only the first would hide the second. Both
    // are surfaced, joined, because a run that mentions one problem while sitting on another is the
    // same false-confidence failure as a run that mentions none.
    private static string? JoinProblems(params string?[] problems)
    {
        List<string> present = new List<string>();
        foreach (string? problem in problems)
        {
            if (problem != null)
                present.Add(problem);
        }

        return present.Count == 0 ? null : string.Join("; ", present);
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
            // The terrain def is named even though it's only one of the reasons CanSpawnAt can refuse,
            // because it's the one a report otherwise can't convey at all — "(128,118) refused" leaves
            // the author guessing, "(128,118) ... terrain 'WaterDeep'" ends the investigation.
            reason = "terrain or an indestructible blocker refuses it " +
                     $"(terrain '{map.terrainGrid.TerrainAt(cell).defName}')";
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

    // ----- clearing ---------------------------------------------------------------------------

    // Inspects one footprint cell and, when the step asked for it, clears it. Shared by PlaceThings'
    // placement cells and every cell of SetTerrain's rect, so the two steps can't drift on what
    // "clear" means. All the branching lives in Shared/SceneClearing.Classify; this is the Verse half.
    //
    // Called for every cell whether clearing was asked for or not, because the roofed-cell count is
    // what lets ReportClearing warn about a roofed footprint the scenario forgot to clear.
    private static void PrepareCell(
        Map map, IntVec3 cell, bool clear, ClearTally tally, List<string> blockersHere)
    {
        if (!cell.InBounds(map))
            return;

        tally.Cells++;
        if (map.roofGrid.RoofAt(cell) != null)
            tally.RoofedCells++;

        if (clear)
        {
            DestroyThingsIn(map, cell, tally, blockersHere);
            StripRoof(map, cell);
        }
    }

    // The thing list is SNAPSHOTTED first. GetThingList hands back the map's own live thingGrid list,
    // and Thing.Destroy despawns — mutating that very list mid-iteration, which would skip things or
    // throw. Copying is cheap next to the destroys it guards.
    private static void DestroyThingsIn(
        Map map, IntVec3 cell, ClearTally tally, List<string> blockersHere)
    {
        List<Thing> present = new List<Thing>(cell.GetThingList(map));
        foreach (Thing thing in present)
            ApplyVerdict(thing, cell, tally, blockersHere);
    }

    private static void ApplyVerdict(
        Thing thing, IntVec3 cell, ClearTally tally, List<string> blockersHere)
    {
        // A multi-cell building destroyed while clearing an earlier cell is still present in this
        // cell's snapshot, and Thing.Destroy Log.Errors on an already-destroyed thing.
        if (thing.Destroyed)
            return;

        ClearVerdict verdict = SceneClearing.Classify(
            thing.def.category.ToString(), thing.def.destroyable, out string? reason);

        switch (verdict)
        {
            case ClearVerdict.Destroy:
                // DestroyMode.Vanish, not KillFinalize: mining a granite wall the vanilla way drops
                // chunks, which would put new blockers into the footprint we just cleared. Vanish
                // leaves nothing behind (GenLeaving.DoLeavingsFor no-ops for it).
                thing.Destroy(DestroyMode.Vanish);
                tally.ThingsDestroyed++;
                break;
            case ClearVerdict.Blocked:
                tally.Blocked.Add($"({cell.x},{cell.z}) {thing.def.defName} — {reason}");
                blockersHere.Add($"{thing.def.defName} ({reason})");
                break;
            default:
                tally.Left.Add($"({cell.x},{cell.z}) {thing.def.defName} — {reason}");
                break;
        }
    }

    // Roof is stripped even where the cell held nothing: overhead mountain roof lives in its own grid
    // and SURVIVES the rock under it being destroyed. That matters more than the placement shortfall
    // that motivated clearing — a roofed cell is darkened, and these scenes exist to be photographed
    // for their lighting, so a pad half under mountain is wrong for the exact thing being measured.
    //
    // SetRoof is the direct write rather than vanilla's collapse-checked removal path. A harness scene
    // is torn down with the process (nothing is ever saved), so an unsupported neighbouring roof slab
    // never gets a chance to matter — and a collapse mid-setup would drop rubble into the footprint.
    // SetRoof no-ops when the cell is already unroofed, so this needs no guard of its own.
    private static void StripRoof(Map map, IntVec3 cell)
    {
        map.roofGrid.SetRoof(cell, null);
    }

    // Says what clearing did — and, just as importantly, says something when it was NOT asked for and
    // the footprint was roofed anyway. Terrain paints and pillars stand perfectly well under overhead
    // mountain; the only symptom is a screenshot that is wrong for the lighting the scenario set out to
    // show. Staying quiet there is exactly the plausible-but-wrong result this harness exists to catch,
    // so it's a Log.Warning rather than nothing — but not a step failure, because a scenario is allowed
    // to want a roofed scene, and turning that into a red run would break existing specs.
    private static string? ReportClearing(string step, bool clear, ClearTally tally)
    {
        if (!clear)
        {
            if (tally.RoofedCells > 0)
                Log.Warning(
                    $"RWTH: {step} built into {tally.RoofedCells} of {tally.Cells} footprint cells that " +
                    $"carry roof — overhead roof darkens them, so lighting screenshots of this scene are " +
                    $"not of open sky. Set \"{StepArgs.SceneClear}\": \"true\" if that isn't intended.");

            return null;
        }

        Log.Message($"RWTH: {step} {SceneClearing.Describe(tally)}");

        // Spared things are logged, never failed: a pawn wandering onto the pad is a fact about the
        // colony, not a defect in the scene, and failing on it would make runs nondeterministic.
        if (tally.Left.Count > 0)
            Log.Message($"RWTH: {step} left {tally.Left.Count} thing(s) in place: " +
                        $"{SceneClearing.FormatList(tally.Left)}");

        return SceneClearing.BlockedError(tally);
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
        ClearTally tally = new ClearTally();

        // Never read back here — SetTerrain has no per-cell verdict to enrich, unlike PlaceThings'
        // refused cells — but PrepareCell needs somewhere to put per-cell blockers. The whole-step
        // tally.Blocked list is what the gate at the bottom reads.
        List<string> blockersHere = new List<string>();

        foreach (IntVec3 cell in rect)
        {
            if (cell.InBounds(map))
            {
                blockersHere.Clear();

                // Cleared before painting, so the paint wins: destroying a floor-bearing building can
                // reset the terrain under it, which would undo a repaint done the other way round.
                PrepareCell(map, cell, plan.Clear, tally, blockersHere);
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

        return ReportClearing(StepArgs.SetTerrainType, plan.Clear, tally);
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

    // ----- SpawnAnimal ------------------------------------------------------------------------

    // Generates wild animals and spawns them on the plan's cells, returning an error describing any
    // shortfall. Counting what actually landed is the same discipline as PlaceThings: an animal asked
    // for on a wall cell would otherwise be silently absent, and a screenshot of a "successful" spawn
    // would show empty ground.
    public static string? SpawnAnimals(Map map, AnimalPlan plan)
    {
        if (!TryResolveAnimalKind(plan.KindDefName, out PawnKindDef kind, out string? kindError))
            return kindError;

        IntVec3 anchor = ResolveAnchor(plan, map);
        List<string> refused = new List<string>();
        ClearTally tally = new ClearTally();

        // Reused across cells rather than allocated per cell, matching PlaceThings — a large row would
        // otherwise churn a list per cell for a case that is almost always empty.
        List<string> blockersHere = new List<string>();
        int spawned = 0;

        foreach (ScenePlacement placement in plan.Cells)
        {
            IntVec3 cell = anchor + new IntVec3(placement.Dx, 0, placement.Dz);
            blockersHere.Clear();

            // Clearing runs BEFORE the standable re-check inside TrySpawnAnimal, exactly as PlaceThings
            // orders it against CanSpawnAt: a wall is destroyable, so clearing turns a cell that would
            // refuse a pawn into one that accepts it, while re-checking afterwards still keeps a
            // genuinely un-standable cell (deep water, an indestructible edifice) an honest refusal.
            PrepareCell(map, cell, plan.Clear, tally, blockersHere);

            if (TrySpawnAnimal(kind, cell, map, out string? reason))
                spawned++;
            else
                refused.Add($"({cell.x},{cell.z}) {reason}{SceneClearing.RefusalDetail(blockersHere)}");
        }

        // Unfog last, after spawning: RimWorld draws nothing in fogged cells, so a scene built at map
        // centre on a fresh colony is invisible while every step still reports success. Reuses the same
        // core PlaceThings/SetTerrain lift fog through.
        if (plan.Unfog)
            Unfog(map);

        // Both failure modes surfaced, joined, like PlaceThings: a run that mentions a refused cell
        // while silently sitting on a blocker clearing couldn't remove is the same false-confidence
        // failure this harness exists to catch.
        return JoinProblems(
            ShortfallError(spawned, plan.Cells.Count, kind.defName, refused),
            ReportClearing(StepArgs.SpawnAnimalType, plan.Clear, tally));
    }

    // errorOnFail: false — a bad kind name is a scenario bug that belongs in the report, not a
    // Log.Error buried in Player.log. A non-animal kind is rejected here rather than generated: a
    // colonist or mechanoid kind would come out of PawnGenerator with faction/gear this step does not
    // yet handle, so the scope limit is enforced, not silently exceeded.
    private static bool TryResolveAnimalKind(string kindDefName, out PawnKindDef kind, out string? error)
    {
        kind = DefDatabase<PawnKindDef>.GetNamed(kindDefName, errorOnFail: false);
        if (kind == null)
        {
            error = $"no PawnKindDef named '{kindDefName}' in the active modset";
            return false;
        }

        // kind.race can be null on a malformed def; guard before touching RaceProps so the report reads
        // as a scenario error rather than a NullReferenceException swallowed mid-run.
        if (kind.race?.race == null || !kind.RaceProps.Animal)
        {
            error = $"'{kindDefName}' is not an animal — SpawnAnimal only spawns wild animals for now";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TrySpawnAnimal(PawnKindDef kind, IntVec3 cell, Map map, out string? reason)
    {
        if (!cell.InBounds(map))
        {
            reason = "out of bounds";
            return false;
        }

        // Standable rather than GenSpawn.CanSpawnAt (which takes a ThingDef, not a PawnKindDef): a pawn
        // needs a cell it can stand in, and a wall or deep water is exactly what Standable rejects. The
        // terrain is named for the same reason PlaceThings names it — "(x,z) refused" leaves the author
        // guessing, "(x,z) ... terrain 'WaterDeep'" ends the investigation.
        if (!cell.Standable(map))
        {
            reason = "cell is not standable " +
                     $"(terrain '{map.terrainGrid.TerrainAt(cell).defName}')";
            return false;
        }

        // Wild animal: null faction is what makes it unaffiliated wildlife, the deliberate default for
        // this first cut. Rot4.South faces the animal toward the camera so screenshots are consistent
        // rather than showing a randomly-turned pawn.
        Pawn pawn = PawnGenerator.GeneratePawn(kind, null);
        GenSpawn.Spawn(pawn, cell, map, Rot4.South);

        reason = null;
        return true;
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
