using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps.BuiltIn;
using UnityEngine;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of LandInOrbit. See Shared/Steps/BuiltIn/LandInOrbitStep.cs for the pure
// half and the rationale; together they are the whole step.
//
// THE PATH THIS FOLLOWS IS VANILLA'S, ON PURPOSE. Every line below is on the same route the game
// itself takes to put a map in orbit:
//
//   WorldGrid.RegisterPlanetLayer(PlanetLayerDefOf.Orbit, ...)  — what WorldGrid.CreateRequiredLayers
//       does at world gen for the Odyssey scenario's ScenPart_PlanetLayer, needed here only for a
//       save that predates the DLC and therefore has no orbit layer in it.
//   PlanetLayer.RunWorldGeneration()                            — WorldGenStep_Tiles, which is what
//       gives each orbit tile PrimaryBiome = its layer def's DefaultBiome (Odyssey's vacuum `Orbit`).
//   GetOrGenerateMapUtility.GetOrGenerateMap(tile, size, def)   — the call SettleInEmptyTileUtility
//       and every transport-pod arrival use. It makes the MapParent world object, registers it, and
//       runs MapGenerator.GenerateMap with that parent's own MapGeneratorDef (SpaceMapParent's is
//       Odyssey's `Space` generator).
//
// The tempting shortcut — generate an ordinary surface map and set inVacuum-looking flags on it — is
// specifically what this must not do. Such a map keeps the surface PlanetLayer, so its lat/long comes
// off the surface sphere; keeps surface terrain, rock and roof; and reads wrong from per-cell
// VacuumUtility.GetVacuum. Probes measuring vacuum lighting against it would be validating a prop.
//
// Registering the map parent is also what makes the layer legal: RimWorld.OrbitLayer.CanSelectLayer
// refuses the layer until Find.WorldObjects.AnyWorldObjectOnLayer(this) is true, and the world object
// GetOrGenerateMap creates satisfies exactly that. This step gets it as a side effect of doing the
// real thing rather than having to arrange it separately.
public sealed class LandInOrbitAction : IStepAction
{
    public string Type => LandInOrbitStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        // SKIP, not fail. Odyssey is a paid DLC; a box without it cannot be made to run this scenario
        // by fixing any mod, so failing here would paint a permanent red on an otherwise healthy
        // install. The driver abandons the rest of the scenario and reports it as skipped.
        if (!ModsConfig.OdysseyActive)
        {
            return StepOutcome.Skip(
                $"{LandInOrbitStep.StepType} needs the Odyssey DLC (orbital maps are its content) and " +
                "it is not active in this run's mod list");
        }

        if (!LandInOrbitStep.TryRead(args, out OrbitRequest request, out string? error))
            return StepOutcome.Fail(error!);

        PlanetLayer? layer = ResolveOrbitLayer(out string? layerError);
        if (layer == null)
            return StepOutcome.Fail(layerError!);

        PlanetTile tile = ResolveTile(layer, request, out double offsetDegrees, out string? tileError);
        if (tileError != null)
            return StepOutcome.Fail(tileError);

        Map? map = GenerateOrbitalMap(tile, request, out bool freshlyGenerated, out string? mapError);
        if (map == null)
            return StepOutcome.Fail(mapError!);

        string? postconditionError = VerifyReallyInOrbit(map, freshlyGenerated);
        if (postconditionError != null)
            return StepOutcome.Fail(postconditionError);

        SwitchTo(map, request);
        LogWhereWeLanded(map, tile, request, offsetDegrees);

        return new StepOutcome
        {
            // The same pin SetTile applies, and for a reason a real tile does not remove: the orbit
            // layer is an icosphere with tiles a couple of degrees wide, so "latitude 45" resolves to
            // a tile at 44.3° or 45.8° depending on the world's seed and view angle. Pinning
            // WorldGrid.LongLatOf means the sun path every probe reads is the latitude the scenario
            // asked for, on every world, instead of whatever the subdivision happened to produce.
            // LogWhereWeLanded prints both numbers so the gap is visible rather than papered over.
            ForcedLatitude = request.Latitude,
            // Map generation leaves a great deal to settle — glow grid, sky, region rebuild — and the
            // very next step is usually a probe or a screenshot.
            WaitFrames = StepHelpers.SceneSettleFrames,
        };
    }

    // The orbit layer if the world has one, else a freshly registered and generated one.
    //
    // A world generated with Odyssey active always has it (WorldGrid.CreateRequiredLayers builds it
    // from the scenario's ScenPart_PlanetLayer), but a FIXTURE SAVE MADE BEFORE THE DLC WAS INSTALLED
    // does not — and the runner activates every installed DLC regardless of what the save was made
    // with, so that combination is not exotic, it is the default for any fixture older than a
    // player's Odyssey purchase. Registering it is the same call world gen makes, with the same
    // settings def, so the layer that results is not a special harness variant of one.
    private static PlanetLayer? ResolveOrbitLayer(out string? error)
    {
        error = null;
        WorldGrid grid = Find.WorldGrid;

        foreach (KeyValuePair<int, PlanetLayer> entry in grid.PlanetLayers)
        {
            if (entry.Value.Def == PlanetLayerDefOf.Orbit)
                return entry.Value;
        }

        PlanetLayerSettingsDef settingsDef = PlanetLayerSettingsDefOf.Orbit;
        if (settingsDef?.settings == null)
        {
            error = "Odyssey is active but PlanetLayerSettingsDefOf.Orbit has no settings — cannot " +
                    "register an orbit layer for a world that lacks one";
            return null;
        }

        Log.Message(
            "RWTH: world has no orbit PlanetLayer (save predates Odyssey?) — registering one from " +
            $"{settingsDef.defName} and running its world generation");

        PlanetLayer layer = grid.RegisterPlanetLayer(PlanetLayerDefOf.Orbit, settingsDef.settings);
        // Without this the layer has geometry but no Tile objects, so every tile lookup on it throws
        // and nothing would say why. WorldGenStep_Tiles is also what stamps the vacuum biome on.
        layer.RunWorldGeneration();
        return layer;
    }

    // The orbit tile nearest the requested lat/long, or a failure naming how far the nearest one was.
    // The selection rule and the tolerance both live in Shared/OrbitTileSelection.cs; this only reads
    // tile centres out of the live layer.
    private static PlanetTile ResolveTile(
        PlanetLayer layer, OrbitRequest request, out double offsetDegrees, out string? error)
    {
        int count = layer.TilesCount;
        List<float> lats = new List<float>(count);
        List<float> lons = new List<float>(count);
        for (int i = 0; i < count; i++)
        {
            Vector2 longLat = layer.LongLatOf(i);
            lons.Add(longLat.x);
            lats.Add(longLat.y);
        }

        int index = OrbitTileSelection.PickNearest(
            lats, lons, request.Latitude, request.Longitude, out offsetDegrees);

        if (index < 0)
        {
            error = $"the orbit layer has no tiles (TilesCount={count})";
            return PlanetTile.Invalid;
        }

        if (offsetDegrees > request.MaxOffsetDegrees)
        {
            // Loud rather than "landed nearby": with stationary orbits, drifting to a latitude the
            // scenario did not ask for silently changes the day length and sun path every probe on
            // that map reads. A planet layer is only generated across the world's view angle, so a
            // small-coverage world genuinely has no tiles in most latitude bands.
            error = $"no orbit tile within {request.MaxOffsetDegrees:0.##}° of the requested " +
                    $"{DescribeRequest(request)} — nearest of {count} tiles is {offsetDegrees:0.##}° " +
                    $"away at {DescribeLongLat(lons[index], lats[index])}. This world's orbit layer " +
                    "only covers the planet's generated view angle; pick a latitude inside it, drop " +
                    $"'{LandInOrbitStep.LongitudeArg}', or raise '{LandInOrbitStep.MaxOffsetArg}'";
            return PlanetTile.Invalid;
        }

        error = null;
        return new PlanetTile(index, layer);
    }

    // Runs Odyssey's own space map generator for the tile. Synchronous rather than queued as a long
    // event: the driver's Tick already refuses to step while ShouldWaitForEvent, so a queued event
    // would run AFTER this step returned and the postcondition checks below would have nothing to
    // check. MapGenerator.GenerateMap does not require a long event to be current — it only sets and
    // restores ProgramState, and LongEventHandler.SetCurrentEventText is a no-op with no event.
    private static Map? GenerateOrbitalMap(
        PlanetTile tile, OrbitRequest request, out bool freshlyGenerated, out string? error)
    {
        error = null;
        freshlyGenerated = false;

        Map existing = Current.Game.FindMap(tile);
        if (existing != null)
        {
            // Re-running a scenario inside one game load (or two scenarios in a suite asking for the
            // same tile) should reuse the platform rather than fail. GetOrGenerateMap would do this
            // itself; saying so out loud matters because a reused map carries the previous
            // scenario's scene, which is a thing a reader of the log needs to know.
            Log.Message($"RWTH: reusing the orbital map already generated at {tile}");
            return existing;
        }

        WorldObjectDef parentDef = tile.LayerDef.DefaultWorldObject;
        if (parentDef == null)
        {
            error = $"PlanetLayerDef '{tile.LayerDef.defName}' declares no defaultMapWorldObject, so " +
                    "there is nothing to hang a map on";
            return null;
        }

        IntVec3 size = request.MapSize > 0
            ? new IntVec3(request.MapSize, 1, request.MapSize)
            : Find.World.info.initialMapSize;

        Log.Message(
            $"RWTH: generating an orbital map at {tile} (layer '{tile.LayerDef.defName}', " +
            $"world object '{parentDef.defName}', size {size.x}x{size.z}) — this takes a while");

        Map map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, size, parentDef);
        if (map == null)
            error = $"GetOrGenerateMapUtility returned no map for orbit tile {tile}";

        freshlyGenerated = map != null;
        return map;
    }

    // The step's own postconditions, checked rather than assumed. This is the difference between a
    // green run meaning "an orbital map was reached" and meaning "a step that intends to reach one
    // did not throw" — and the specific fake it rules out (a surface map wearing vacuum flags) is one
    // no probe downstream could tell apart on its own.
    private static string? VerifyReallyInOrbit(Map map, bool freshlyGenerated)
    {
        if (map.Tile.LayerDef != PlanetLayerDefOf.Orbit)
        {
            return $"generated map is on PlanetLayerDef '{map.Tile.LayerDef?.defName ?? "null"}', not " +
                   $"'{PlanetLayerDefOf.Orbit.defName}' — it is not an orbital map";
        }

        BiomeDef biome = map.Biome;
        if (biome == null || !biome.inVacuum)
        {
            return $"generated map's biome '{biome?.defName ?? "null"}' is not inVacuum — the space " +
                   "map generator did not produce a vacuum map";
        }

        // The per-cell check, which is the one a dressed-up surface map cannot fake: biome and layer
        // are single values anything could assign, whereas GetVacuum reads the room the cell is
        // actually in. Gated on a FRESH map only — a reused one may have had a pressurised room built
        // over its centre by the scenario that generated it, and failing over that would be reporting
        // a successful earlier scenario as a broken later one.
        if (freshlyGenerated && map.Center.GetVacuum(map) <= 0f)
        {
            return $"generated map's centre cell reads GetVacuum={map.Center.GetVacuum(map):0.###} — " +
                   "an open cell on a freshly generated orbital platform should be in vacuum";
        }

        return null;
    }

    private static void SwitchTo(Map map, OrbitRequest request)
    {
        Current.Game.CurrentMap = map;

        // Odyssey's space generator fogs the whole map, and RimWorld draws nothing in a fogged cell,
        // so without this a screenshot of a successfully generated orbital map is a black rectangle
        // on a green run — the same trap StepArgs.SceneUnfog defaults to true for.
        if (request.Unfog)
            map.fogGrid.ClearAllFog();

        // The camera is still pointed at wherever the previous map was looking, which on a different
        // map is an arbitrary corner. Centre it; a scenario wanting a specific framing follows with
        // LookAt, exactly as it would on the surface.
        Find.CameraDriver.JumpToCurrentMapLoc(map.Center);
    }

    // Both the tile's real lat/long and the pinned one, because they differ by design and a reader
    // reconstructing a probe value months later needs to know which of the two it was computed from.
    private static void LogWhereWeLanded(
        Map map, PlanetTile tile, OrbitRequest request, double offsetDegrees)
    {
        Vector2 longLat = tile.Layer.LongLatOf(tile);
        Log.Message(
            $"RWTH: in orbit — map {map.uniqueID} on tile {tile.tileId} of layer " +
            $"'{tile.LayerDef.defName}' ({tile.LayerDef.elevationString}), biome " +
            $"'{map.Biome.defName}' (inVacuum={map.Biome.inVacuum}, " +
            $"centre-cell GetVacuum={map.Center.GetVacuum(map):0.###}), tile at " +
            $"{DescribeLongLat(longLat.x, longLat.y)}, {offsetDegrees:0.##}° from the requested " +
            $"{DescribeRequest(request)}; latitude pinned to {request.Latitude:0.##}° for the rest " +
            "of the run");
    }

    private static string DescribeRequest(OrbitRequest request) =>
        request.Longitude is float longitude
            ? DescribeLongLat(longitude, request.Latitude)
            : $"lat {request.Latitude:0.##}° (any longitude)";

    private static string DescribeLongLat(float longitude, float latitude) =>
        $"long {longitude:0.##}°, lat {latitude:0.##}°";
}
