using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;

namespace RimWorldTestHarness.Mod;

// Backs the "SetTile" scenario step (StepArgs.SetTileType). Rather than actually regenerating the
// fixture's landing tile (which would drag in WorldGenerator and defeat the point of a reusable
// fixture — see Fixtures/README.md), a scenario just overrides the latitude every caller of
// WorldGrid.LongLatOf sees, including CelestialLighting's own LatitudeEffect.ForMap. Longitude is
// left untouched: nothing in StepArgs exposes a longitude override (see its own comment), and
// leaving it real means SetSeason/SetTime's day/hour math (which depends on longitude for the
// local-time offset) keeps behaving normally.
[HarmonyPatch(typeof(WorldGrid), nameof(WorldGrid.LongLatOf))]
public static class Patch_ForcedLatitude
{
    static void Postfix(ref Vector2 __result)
    {
        if (HarnessRuntime.ForcedLatitude is float latitude)
            __result.y = latitude;
    }
}
