using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// The parsed form of a LandInOrbit step's args. Public because the executing half re-reads the same
// args at run time and must not re-derive the parsing rules — same split SceneLayout uses.
public sealed class OrbitRequest
{
    public float Latitude;

    // null => "any longitude". See LandInOrbitStep's comment on the arg.
    public float? Longitude;

    public float MaxOffsetDegrees = OrbitTileSelection.DefaultMaxOffsetDegrees;

    // 0 => use the world's own initialMapSize, which is what every other map in the save uses.
    public int MapSize;

    public bool Unfog = true;
}

// LandInOrbit — generate a real Odyssey orbital map at a pinned latitude and switch to it.
//
// WHY A SCENARIO WOULD WANT THIS. Odyssey puts orbital maps on a separate PlanetLayer
// (PlanetLayerDefOf.Orbit), whose tiles carry the vacuum `Orbit` biome and sit 200 km up. Everything
// a mod does differently in vacuum — no atmospheric scattering, no airglow, no weather worth the
// name, a different night-light budget — only manifests there, and no combination of the existing
// steps could reach it: SetTile overrides latitude on the map you already have, and SetBiome repaints
// the tile under it. Both leave a SURFACE map wearing orbital clothes: still on the surface layer,
// still with surface-generated terrain and rock, still with per-cell VacuumUtility.GetVacuum reading
// like a planet. Probes run against that would validate against a fake and report green.
//
// So this step does the real thing: it resolves a genuine orbit-layer tile, runs Odyssey's own space
// MapGeneratorDef through the ordinary MapGenerator entry point (via GetOrGenerateMapUtility, the
// same call vanilla settling uses), and makes the result the current map. The biome, the layer and
// the vacuum are arrived at by generation, not assignment.
//
// WHY LATITUDE IS REQUIRED AND NOT OPTIONAL. RimWorld's orbits are stationary — PlanetLayer.LongLatOf
// derives lat/long from a fixed tile centre, and nothing gives an orbit tile a period or a phase. The
// platform therefore hangs over one lat/long forever, and that lat/long alone fixes its day length
// and sun path. A scenario that landed "somewhere in orbit" would produce numbers nobody could pin,
// which is the whole reason to have a harness.
//
// See Mod/Steps/BuiltIn/LandInOrbitAction.cs for the executing half; together they are the step.
public sealed class LandInOrbitStep : IStepSpec
{
    public const string StepType = "LandInOrbit";

    public const string LatitudeArg = "latitude";   // float degrees, -90..90. Required.

    // Optional. Omit it and the step takes the nearest tile at the requested LATITUDE regardless of
    // longitude, which is almost always what a lighting scenario wants: longitude does not change the
    // sun's elevation over a tile, only the local-time offset that SetTime/SetSeason already read off
    // the real tile. It also matters practically — a planet layer is only generated across the
    // world's view angle, so on a small-coverage world a specific lat/long pair may never have been
    // generated at all while its latitude band has plenty of tiles.
    public const string LongitudeArg = "longitude"; // float degrees, -180..180

    // How far the resolved tile may sit from the request before the step fails. See
    // OrbitTileSelection.DefaultMaxOffsetDegrees for why the default is what it is; raise it
    // deliberately if a scenario genuinely does not care where it lands, and expect the report to say
    // how far off it ended up either way.
    public const string MaxOffsetArg = "maxOffsetDegrees"; // float > 0, default 5

    // Cells per side. Omit to use the world's own initialMapSize, matching every other map in the
    // save; a smaller one is worth setting for a probe-only scenario, since map generation is by far
    // the most expensive thing this step does.
    public const string MapSizeArg = "mapSize"; // int 50..1000

    // Lift fog of war on the generated map. Defaults TRUE, for the reason StepArgs.SceneUnfog gives:
    // RimWorld draws nothing in fogged cells, and Odyssey's space map generator fogs the lot, so a
    // screenshot of a freshly generated orbital map is otherwise a black rectangle on a green run.
    public const string UnfogArg = "unfog"; // bool, default true

    private static readonly string[] KnownArgs =
    {
        LatitudeArg, LongitudeArg, MaxOffsetArg, MapSizeArg, UnfogArg,
    };

    public string Type => StepType;

    // Latitude, because this pins HarnessRuntime.ForcedLatitude exactly as SetTile does (see the
    // action for why a real tile is not enough on its own), plus NewMap, because a whole extra Map now
    // exists and the game is looking at it. NewMap is deliberately not soft-resettable: a following
    // scenario that believed itself isolated would otherwise open on the orbital platform instead of
    // the fixture colony and quietly measure the wrong world.
    public ScenarioResidue Residue => ScenarioResidue.NewMap | ScenarioResidue.Latitude;

    // Emphatically not live-callable. It generates a map into a real player's save and moves them to
    // it, and nothing about that is "minimally invasive".
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        TryRead(args, out _, out error);

    // Shared by validation and execution so a scenario that loaded clean cannot then be re-read
    // differently at run time.
    public static bool TryRead(
        IReadOnlyDictionary<string, string> args, out OrbitRequest request, out string? error)
    {
        request = new OrbitRequest();

        if (!ArgReader.ValidateKnownArgs(args, KnownArgs, out error))
            return false;

        if (!TryReadLatitude(args, request, out error))
            return false;

        if (!TryReadLongitude(args, request, out error))
            return false;

        if (!TryReadMaxOffset(args, request, out error))
            return false;

        if (!TryReadMapSize(args, request, out error))
            return false;

        return ArgReader.TryReadBool(args, UnfogArg, true, out request.Unfog, out error);
    }

    private static bool TryReadLatitude(
        IReadOnlyDictionary<string, string> args, OrbitRequest request, out string? error)
    {
        if (!args.ContainsKey(LatitudeArg))
        {
            error = $"'{LatitudeArg}' is required — orbits are stationary, so the platform's latitude " +
                    "is what fixes its day length and sun path, and a scenario that does not pin it " +
                    "produces numbers nothing can be pinned against";
            return false;
        }

        if (!ArgReader.TryReadDouble(args, LatitudeArg, 0, out double latitude, out error))
            return false;

        if (latitude < -90 || latitude > 90)
        {
            error = $"'{LatitudeArg}' must be between -90 and 90 (got '{args[LatitudeArg]}')";
            return false;
        }

        request.Latitude = (float)latitude;
        return true;
    }

    private static bool TryReadLongitude(
        IReadOnlyDictionary<string, string> args, OrbitRequest request, out string? error)
    {
        if (!args.ContainsKey(LongitudeArg))
        {
            request.Longitude = null;
            error = null;
            return true;
        }

        if (!ArgReader.TryReadDouble(args, LongitudeArg, 0, out double longitude, out error))
            return false;

        if (longitude < -180 || longitude > 180)
        {
            error = $"'{LongitudeArg}' must be between -180 and 180 (got '{args[LongitudeArg]}')";
            return false;
        }

        request.Longitude = (float)longitude;
        return true;
    }

    private static bool TryReadMaxOffset(
        IReadOnlyDictionary<string, string> args, OrbitRequest request, out string? error)
    {
        if (!ArgReader.TryReadDouble(
                args, MaxOffsetArg, OrbitTileSelection.DefaultMaxOffsetDegrees,
                out double maxOffset, out error))
            return false;

        if (maxOffset <= 0)
        {
            error = $"'{MaxOffsetArg}' must be greater than 0 (got '{args[MaxOffsetArg]}')";
            return false;
        }

        request.MaxOffsetDegrees = (float)maxOffset;
        return true;
    }

    private static bool TryReadMapSize(
        IReadOnlyDictionary<string, string> args, OrbitRequest request, out string? error)
    {
        if (!ArgReader.TryReadInt(args, MapSizeArg, 0, out int mapSize, out error))
            return false;

        // 0 is the "use the world default" sentinel; anything else has to be a size RimWorld will
        // actually generate. The bounds mirror the vanilla world-creation slider rather than being
        // invented here.
        if (mapSize != 0 && (mapSize < 50 || mapSize > 1000))
        {
            error = $"'{MapSizeArg}' must be between 50 and 1000 cells, or omitted for the world's " +
                    $"own map size (got '{args[MapSizeArg]}')";
            return false;
        }

        request.MapSize = mapSize;
        return true;
    }
}
