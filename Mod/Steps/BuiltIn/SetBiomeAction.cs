using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using RimWorldTestHarness.Shared.Steps.BuiltIn;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of SetBiome. See Shared/Steps/BuiltIn/SetBiomeStep.cs for the pure half and
// the rationale; together they are the whole step.
public sealed class SetBiomeAction : IStepAction
{
    public string Type => SetBiomeStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string defName = args[SetBiomeStep.BiomeDefArg];
        // GetNamedSilentFail, matching SetWeather: an unknown defName should fail this step with a
        // readable reason rather than throw inside the tick loop.
        BiomeDef def = DefDatabase<BiomeDef>.GetNamedSilentFail(defName);
        if (def == null)
            return StepOutcome.Fail($"No BiomeDef named '{defName}'");

        // Map.Biome is a read-only property forwarding to Tile.PrimaryBiome, which does have a setter —
        // so the tile is where the write goes. Map.TileInfo already resolves pocket maps to their own
        // pocketTileInfo, so this works on an Undercave or a gravship as well as on a surface colony.
        Tile tile = ctx.Map.TileInfo;
        if (tile == null)
            return StepOutcome.Fail("Map has no TileInfo to set a biome on");

        tile.PrimaryBiome = def;

        // A frame for the sky to be recomputed against the new biome, matching what the scene steps do
        // after changing geometry. SkyManagerUpdate reads Map.Biome every frame, so one settle frame is
        // enough for anything gated on disableSkyLighting or the weather census to take effect.
        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}
