using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using RimWorldTestHarness.Shared.Steps.BuiltIn;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of EnterPocketMap. See Shared/Steps/BuiltIn/EnterPocketMapStep.cs for the
// pure half and the rationale; together they are the whole step.
//
// THE PATH THIS FOLLOWS IS VANILLA'S, ON PURPOSE, exactly as LandInOrbitAction's is. There is only
// one call, and three separate places in vanilla make it identically:
// CompObelisk_Abductor.GenerateLabyrinth, MapPortal.GeneratePocketMapInt, and
// DebugActionsMapManagement's own pocket-map menu all reach
// PocketMapUtility.GeneratePocketMap(size, generatorDef, extraGenSteps, sourceMap). Going through it
// is what makes the resulting map genuinely tileless rather than a surface map we have lied about:
// GeneratePocketMap creates a PocketMapParent, never assigns its tile, and passes isPocketMap: true
// to MapGenerator.GenerateMap, which is the flag that sets map.info.isPocketMap and builds the
// map's own pocketTileInfo from the generator's pocketMapProperties.
//
// There is deliberately no "reuse an existing one" path, unlike LandInOrbit's. Orbit is keyed by a
// tile, so asking twice for the same latitude should land on the same platform; pocket maps have no
// key at all — vanilla itself tracks them by holding a reference on the obelisk or portal that made
// one. A scenario asking for two labyrinths means two labyrinths.
public sealed class EnterPocketMapAction : IStepAction
{
    public string Type => EnterPocketMapStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string defName = args[EnterPocketMapStep.MapGeneratorArg];

        // SKIP, not fail, when the scenario named a provider and it is not in this run. Same rule and
        // same reason as LandInOrbit's Odyssey check (CONTRIBUTING.md, "Steps that need a DLC"): no
        // code change can make this box run the step, so a red here would be permanent and
        // uninformative. Checked BEFORE the def lookup, because a missing expansion is precisely why
        // the lookup would be about to fail.
        if (args.TryGetValue(EnterPocketMapStep.DlcArg, out string? provider)
            && !string.IsNullOrWhiteSpace(provider)
            && !ProviderIsActive(provider))
        {
            return StepOutcome.Skip(
                $"{EnterPocketMapStep.StepType} needs '{provider.Trim()}' (it provides the " +
                $"'{defName}' map generator) and it is not active in this run's mod list");
        }

        // GetNamedSilentFail, matching SetBiome and SetWeather: an unknown defName should fail this
        // step with a readable reason rather than throw inside the tick loop. A FAIL rather than a
        // skip is right here precisely because the skip above has already accounted for the one cause
        // that no code change can fix; what is left is a typo, and a typo must be red.
        MapGeneratorDef def = DefDatabase<MapGeneratorDef>.GetNamedSilentFail(defName);
        if (def == null)
        {
            return StepOutcome.Fail(
                $"No MapGeneratorDef named '{defName}' — check the spelling, and if the DLC or mod " +
                $"providing it may be absent, name it in '{EnterPocketMapStep.DlcArg}' so this skips " +
                "instead of failing");
        }

        // A pocket generator is exactly one that declares pocketMapProperties; that is the field
        // MapGenerator.GenerateMap reads for the map's biome and tile mutators. Handing it a surface
        // generator does not throw — it produces a pocket map whose pocketTileInfo has a NULL
        // PrimaryBiome, so Map.Biome is null and every biome read downstream NullReferences several
        // frames later, somewhere with no connection to this step. Refuse it here instead.
        if (def.pocketMapProperties == null)
        {
            return StepOutcome.Fail(
                $"MapGeneratorDef '{defName}' has no pocketMapProperties, so it is a surface generator " +
                "rather than a pocket one (try Labyrinth, MetalHell, Undercave, AncientStockpile, ...)");
        }

        if (!EnterPocketMapStep.TryReadSize(args, out int size, out string? sizeError))
            return StepOutcome.Fail(sizeError!);

        // sourceMap is what the pocket map is generated "from" — vanilla passes the obelisk's or
        // portal's own map, and GetOrGenerateMapUtility passes Find.AnyPlayerHomeMap. It is used for
        // faction and gravship context rather than for geometry, so the scenario's current map is the
        // right answer and is also the only one guaranteed to exist here.
        Map map = PocketMapUtility.GeneratePocketMap(
            new IntVec3(size, 1, size), def, null, ctx.Map);

        if (map == null)
            return StepOutcome.Fail($"PocketMapUtility.GeneratePocketMap returned no map for '{defName}'");

        string? postconditionError = VerifyReallyAPocketMap(map);
        if (postconditionError != null)
            return StepOutcome.Fail(postconditionError);

        SwitchTo(map, args);
        LogWhereWeLanded(map, def, size);

        return new StepOutcome
        {
            // Map generation leaves a great deal to settle — glow grid, sky, region rebuild — and the
            // very next step is usually a probe or a screenshot. Same pin LandInOrbit takes.
            WaitFrames = StepHelpers.SceneSettleFrames,
        };
    }

    // An expansion name ("Anomaly") or any packageId ("ludeon.rimworld.anomaly", "some.workshop.mod").
    //
    // Both spellings are accepted rather than one, because the two readers are different: a scenario
    // author writing an Anomaly test thinks "Anomaly", while a mod-provided generator has no name but
    // its packageId. Trying the raw value first means a packageId that happens to look like a word
    // still resolves as itself.
    private static bool ProviderIsActive(string provider)
    {
        string trimmed = provider.Trim();

        if (ModsConfig.IsActive(trimmed))
            return true;

        // Ludeon's own expansions all follow this pattern, so "Anomaly" resolves without a lookup
        // table that would need editing for every future DLC.
        return ModsConfig.IsActive("ludeon.rimworld." + trimmed.ToLowerInvariant());
    }

    // The postcondition that makes this step worth having rather than a slower SetBiome.
    //
    // Asserted rather than assumed, for the reason LandInOrbitAction gives about its own vacuum check:
    // the failure this step exists to expose is silent, so a step that quietly produced a tile-bearing
    // map would hand every scenario built on it a green run that verified nothing. If a future
    // RimWorld starts assigning pocket maps a real tile, this is the line that says so — and at that
    // point every mod's pocket-map tile guard becomes dead code, which is worth being told about
    // loudly rather than discovering as an unexplained coverage gap.
    private static string? VerifyReallyAPocketMap(Map map)
    {
        if (!map.IsPocketMap)
            return "generated map does not report IsPocketMap — PocketMapUtility did not do what it says";

        if (map.Tile.Valid)
        {
            return $"generated pocket map has a VALID world tile ({map.Tile.tileId}) — the whole point " +
                   "of this step is a map with PlanetTile.Invalid, so a scenario using it to exercise " +
                   "tile guards would pass without ever reaching them";
        }

        if (map.Biome == null)
            return "generated pocket map has no Biome — its generator's pocketMapProperties named none";

        return null;
    }

    private static void SwitchTo(Map map, IReadOnlyDictionary<string, string> args)
    {
        Current.Game.CurrentMap = map;

        // Pocket generators fog their maps (the labyrinth's whole point is not seeing round the
        // corner), and RimWorld draws nothing in a fogged cell — so without this a screenshot of a
        // successfully generated labyrinth is a black rectangle on a green run. Same trap, same
        // default, same opt-out as LandInOrbit's.
        bool unfog = !args.TryGetValue(EnterPocketMapStep.UnfogArg, out string? raw)
            || !bool.TryParse(raw?.Trim(), out bool parsed)
            || parsed;
        if (unfog)
            map.fogGrid.ClearAllFog();

        // The camera is still pointed at wherever the previous map was looking, which on a different
        // map is an arbitrary corner. Centre it; a scenario wanting a specific framing follows with
        // LookAt, exactly as it would on the surface.
        Find.CameraDriver.JumpToCurrentMapLoc(map.Center);
    }

    // The tile id is logged explicitly, and it is the interesting number rather than a formality: it
    // is the -1 that every guard this step exists to exercise is guarding against, and a reader
    // reconstructing a probe value months later should be able to see it was really absent.
    private static void LogWhereWeLanded(Map map, MapGeneratorDef def, int size)
    {
        Log.Message(
            $"RWTH: in a pocket map — map {map.uniqueID} from generator '{def.defName}' at {size}x{size}, " +
            $"biome '{map.Biome.defName}', IsPocketMap={map.IsPocketMap}, " +
            $"tile={map.Tile.tileId} (Valid={map.Tile.Valid})");
    }
}
