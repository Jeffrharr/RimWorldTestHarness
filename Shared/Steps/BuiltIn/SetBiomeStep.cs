using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// SetBiome — repoint the map's world tile at a different BiomeDef.
//
// WHY A SCENARIO WOULD WANT THIS. A surprising amount of vanilla lighting and weather behaviour is
// gated on the biome rather than on the map: SkyManagerUpdate stops writing the light overlay at all
// when `BiomeDef.disableSkyLighting` is set, `disableShadows` suppresses cast shadows outright, and
// `baseWeatherCommonalities` is what decides whether a map has changeable weather in the first place.
// A mod that branches on any of those has, until now, been untestable here for one dull reason: the
// only save fixture is a temperate forest, and there was no way to ask "what does this look like
// underground?" without hand-authoring a second multi-megabyte save per biome.
//
// This file plus Mod/Steps/BuiltIn/SetBiomeAction.cs is the entire step — no switch arm, no
// registration call, per CONTRIBUTING.md's "Adding a step".
public sealed class SetBiomeStep : IStepSpec
{
    public const string StepType = "SetBiome";
    public const string BiomeDefArg = "biomeDef";   // BiomeDef defName, e.g. "Undercave", "IceSheet"

    public string Type => StepType;

    // Its own residue flag rather than folding into Map, following the reasoning SetWeather's flag
    // was added under: the suite planner decides isolation from these flags, and a biome swap dirties
    // something none of the existing kinds describes. Under-reporting here is the one failure that is
    // silent — the next scenario would run against a world it believes untouched.
    //
    // Deliberately NOT soft-resettable. Restoring it looks trivial (stash the outgoing BiomeDef, put
    // it back) but a biome change is not confined to the one field: temperature, plant and animal
    // density, ambient sound and the world-map tile graphic all read off it, and RimWorld caches parts
    // of that. Requiring a reload is slow and certainly correct, which is the right side to err on.
    public ScenarioResidue Residue => ScenarioResidue.Biome;

    // Emphatically not live-callable. This rewrites a tile of a real player's world.
    public bool LiveCallable => false;

    // The defName cannot be resolved without a loaded DefDatabase, so that check lives in the action.
    // Its presence can be checked offline, and is.
    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!args.TryGetValue(BiomeDefArg, out string? defName) || string.IsNullOrWhiteSpace(defName))
        {
            error = $"'{BiomeDefArg}' is required (a BiomeDef defName, e.g. \"Undercave\")";
            return false;
        }

        error = null;
        return true;
    }
}
