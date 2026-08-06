using System.Collections.Generic;
using System.Globalization;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// SetTileProperties — overwrite scalar fields on the map's world Tile.
//
// WHY THIS IS SEPARATE FROM SetTile. SetTile forces LATITUDE, and it does so without touching world
// state at all: HarnessRuntime.ForcedLatitude is read by Patch_ForcedLatitude, so the real tile is
// never modified and the override can be cleared. The fields here are the opposite — elevation,
// pollution and rainfall are genuinely mutable public fields on RimWorld.Planet.Tile, so this step
// writes through to world state (hence ScenarioResidue.TileProperties, which is reload-only).
//
// Folding these into SetTile would have put two different mechanisms behind one verb, and a scenario
// author would have no way to tell from the JSON which of their arguments left something behind.
//
// WHY THESE THREE. They are the tile scalars that visibly drive lighting, and none of them could be
// reached from a scenario before:
//   * elevation — metres above sea level. Sets how much atmosphere is overhead, so it drives how
//     reddened a low sun gets. A mountain tile and a coastal one are different skies.
//   * pollution — Biotech's 0..1 industrial haze. An aerosol loading.
//   * rainfall — mm/year. Stands in for climate/humidity, which is one reasonable way to key
//     aerosol particle size.
// Every one of them is a field on BASE RimWorld.Planet.Tile, present with no DLC installed (Biotech
// ships pollution's *content*, not the field), so this step needs no DLC gating.
//
// All three are optional and at least one is required. Omitting one leaves it alone rather than
// resetting it to a default — a scenario that wants a polluted mountain says so in one step, and a
// scenario that only cares about altitude does not have to know what the tile's rainfall was.
public sealed class SetTilePropertiesStep : IStepSpec
{
    public const string StepType = "SetTileProperties";
    public const string ElevationArg = "elevation";   // float metres above sea level
    public const string PollutionArg = "pollution";   // float 0..1
    public const string RainfallArg = "rainfall";     // float mm/year, >= 0

    public string Type => StepType;
    public ScenarioResidue Residue => ScenarioResidue.TileProperties;

    // Not live-callable. These write to the world tile of a real player's colony, and pollution in
    // particular is gameplay-visible (it drives toxic buildup), so this stays on the batch channel
    // like every other world-mutating step.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        bool any = args.ContainsKey(ElevationArg)
                   || args.ContainsKey(PollutionArg)
                   || args.ContainsKey(RainfallArg);

        if (!any)
        {
            error = $"at least one of '{ElevationArg}', '{PollutionArg}' or '{RainfallArg}' is required";
            return false;
        }

        // Elevation is deliberately unbounded below: RimWorld's own worldgen produces negative
        // elevations for ocean tiles, and a scenario probing what a below-sea-level map does is a
        // legitimate thing to want.
        if (!TryReadOptional(args, ElevationArg, out _, out error))
            return false;

        if (!TryReadOptional(args, PollutionArg, out float pollution, out error))
            return false;

        // Range-checked rather than clamped, for the reason SetSnow gives about its depth: a value
        // outside 0..1 almost certainly means the author thought this was a percentage, and clamping
        // would run their scenario successfully against the wrong world.
        if (args.ContainsKey(PollutionArg) && (pollution < 0f || pollution > 1f))
        {
            error = $"'{PollutionArg}' must be between 0 and 1 (got {pollution.ToString(CultureInfo.InvariantCulture)})";
            return false;
        }

        if (!TryReadOptional(args, RainfallArg, out float rainfall, out error))
            return false;

        if (args.ContainsKey(RainfallArg) && rainfall < 0f)
        {
            error = $"'{RainfallArg}' cannot be negative (got {rainfall.ToString(CultureInfo.InvariantCulture)})";
            return false;
        }

        error = null;
        return true;
    }

    // InvariantCulture explicitly: scenario JSON is committed and shared between machines, so "1.5"
    // must not become invalid on a machine whose locale uses a decimal comma.
    private static bool TryReadOptional(
        IReadOnlyDictionary<string, string> args,
        string key,
        out float value,
        out string? error)
    {
        value = 0f;

        if (!args.TryGetValue(key, out string? raw))
        {
            error = null;
            return true;
        }

        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"'{key}' must be a number (got '{raw}')";
            return false;
        }

        error = null;
        return true;
    }
}
