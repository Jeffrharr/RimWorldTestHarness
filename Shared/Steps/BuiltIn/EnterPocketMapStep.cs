using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// EnterPocketMap — generate a real pocket map (Anomaly's labyrinth, metal hell or undercave,
// Odyssey's ancient stockpile or insect lair, a modded MapPortal's) and switch to it.
//
// WHY A SCENARIO WOULD WANT THIS, and why SetBiome is not a substitute. A pocket map differs from
// every other map in the game by one structural fact that no BiomeDef can express: it has no world
// tile. PocketMapUtility.GeneratePocketMap builds a PocketMapParent and never assigns its tile, so it
// keeps WorldObject's own initialiser — PlanetTile.Invalid, tileId -1 — for the map's whole life.
//
// That number is load-bearing, because vanilla's tile accessors disagree about it and every one of
// the disagreements is silent:
//
//   Find.WorldGrid[tile]        subscripts the backing List<Tile> UNCHECKED -> ArgumentOutOfRangeException
//   PlanetLayer's own indexer   bounds-checks, returns null
//   WorldGrid.LongLatOf(tile)   substitutes the player's home-map tile, or (0,0) if there is none
//   Map.TileInfo                resolves the map's pocketTileInfo, so biome reads are always fine
//
// So any mod that reads a map's tile has three quite different bugs available to it here — throwing
// every frame, dereferencing a null, or confidently answering about a colony on the other side of the
// planet — and none of them can be reached by any other step. SetBiome repaints the tile a map
// already has, which leaves the tile perfectly valid; it is the exact shortcut LandInOrbit's header
// rejects for orbit, one layer down. A CelestialLighting bug found in the wild is the worked example:
// its cloud-cover estimate called GenTemperature.GetTemperatureFromSeasonAtTile, that reached
// Find.WorldGrid's unchecked indexer, and an obelisk's labyrinth threw out of SkyManagerUpdate on
// every rendered frame — taking the whole mod's sky rendering with it. Every scenario that repo had
// ran on a tile-bearing map, so nothing caught it.
//
// It is also the only way to reach the pocket generators' own content: the labyrinth's endless
// corridors, metal hell's terrain, the undercave's fleshmass. Those are generated, not painted.
//
// WHY THE GENERATOR IS NAMED AND NOT THE EVENT. Triggering the obelisk incident would be more
// faithful and much less useful: it needs an obelisk built, an interactor pawn, an activation ramp
// and a psychic ritual, all of which are content that changes between builds, and none of which the
// map generation actually depends on — CompObelisk_Abductor's own line is a bare
// PocketMapUtility.GeneratePocketMap(new IntVec3(90, 1, 90), MapGeneratorDefOf.Labyrinth, null, ...).
// Naming the MapGeneratorDef reaches the same map by the same call, deterministically, and works
// equally for generators that have no event at all.
//
// See Mod/Steps/BuiltIn/EnterPocketMapAction.cs for the executing half; together they are the step.
public sealed class EnterPocketMapStep : IStepSpec
{
    public const string StepType = "EnterPocketMap";

    // MapGeneratorDef defName. Required, and deliberately not defaulted to Labyrinth: which pocket
    // map a scenario means is exactly the thing a reader should not have to guess.
    public const string MapGeneratorArg = "mapGenerator";

    // Square edge length in cells. Optional.
    public const string SizeArg = "size";

    // Whether to lift fog on arrival. Optional, defaults true — see the action.
    public const string UnfogArg = "unfog";

    // Which expansion or mod provides the generator. Optional, and the reason it exists is
    // CONTRIBUTING.md's rule that a step blocked by an inactive expansion must Skip rather than Fail:
    // a box without Anomaly cannot be made to run a labyrinth scenario by fixing any code, so failing
    // would paint a permanent red on a healthy install.
    //
    // It has to be an arg rather than a hard-coded check the way LandInOrbit's ModsConfig.OdysseyActive
    // is, because this step's subject is a bare defName that may come from Anomaly, from Odyssey, or
    // from a mod, and nothing about the string says which. Without it the step cannot tell "you do not
    // own Anomaly" from "you typed Labyrnth" — and guessing in the Skip direction would silently green
    // a scenario whose whole subject never generated.
    //
    // Takes an expansion name ("Anomaly", "Odyssey") or any packageId ("ludeon.rimworld.anomaly",
    // "some.workshop.mod"); see the action for the resolution.
    public const string DlcArg = "dlc";

    // Vanilla's own debug action (DebugActionsMapManagement.GeneratePocketMap) uses 100x100 for every
    // generator, so that is the default here. The obelisk uses 90 for the labyrinth specifically and
    // MapPortal reads def.portal.pocketMapSize; a scenario that cares passes `size`. Nothing in the
    // pocket generators requires a particular edge length, which is why vanilla itself is willing to
    // use a round number for all of them.
    public const int DefaultSize = 100;

    public string Type => StepType;

    // NewMap, for exactly LandInOrbit's reason: a whole extra Map now exists and the game is looking
    // at it, and a following scenario that believed itself isolated would open on the labyrinth
    // instead of the fixture colony and quietly measure the wrong world.
    //
    // No Latitude, unlike LandInOrbit — and the difference is the point of the step rather than an
    // omission. There is no tile to pin a latitude to.
    public ScenarioResidue Residue => ScenarioResidue.NewMap;

    // Emphatically not live-callable. It generates a map into a real player's save and moves them to
    // it, which is the opposite of minimally invasive.
    public bool LiveCallable => false;

    // The defName cannot be resolved without a loaded DefDatabase, so that check lives in the action;
    // its presence, and the shape of the optional args, are checkable offline and are checked here.
    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!args.TryGetValue(MapGeneratorArg, out string? defName) || string.IsNullOrWhiteSpace(defName))
        {
            error = $"'{MapGeneratorArg}' is required (a MapGeneratorDef defName, e.g. \"Labyrinth\")";
            return false;
        }

        if (!TryReadSize(args, out _, out error))
            return false;

        error = null;
        return true;
    }

    // Shared with the action so the two halves cannot disagree about what `size` means — the same
    // split OrbitRequest exists for.
    public static bool TryReadSize(
        IReadOnlyDictionary<string, string> args, out int size, out string? error)
    {
        size = DefaultSize;
        error = null;

        if (!args.TryGetValue(SizeArg, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return true;

        if (!int.TryParse(raw.Trim(), out size))
        {
            error = $"'{SizeArg}' must be a whole number of cells, got '{raw}'";
            return false;
        }

        // A floor rather than a warning: RimWorld's own map-size minimum is 50, and a smaller pocket
        // map does not fail loudly — the generator simply produces something the layout genstep
        // cannot fill, which photographs as an empty box on a green run.
        if (size < 50)
        {
            error = $"'{SizeArg}' must be at least 50 cells, got {size}";
            return false;
        }

        return true;
    }
}
