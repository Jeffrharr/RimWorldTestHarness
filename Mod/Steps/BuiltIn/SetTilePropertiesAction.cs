using System.Collections.Generic;
using System.Globalization;
using RimWorld.Planet;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of SetTileProperties. See Shared/Steps/BuiltIn/SetTilePropertiesStep.cs for
// the pure half and the rationale; together they are the whole step.
//
// Thin by construction: the spec has already parsed and range-checked everything, so this is only
// the seam between those values and the live Tile.
public sealed class SetTilePropertiesAction : IStepAction
{
    public string Type => SetTilePropertiesStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        // Map.TileInfo rather than Find.WorldGrid[map.Tile]: it already resolves pocket maps to their
        // own pocketTileInfo, and WorldGrid's indexer subscripts its backing list unchecked, so it
        // throws rather than returning null on a pocket map's PlanetTile.Invalid. SetBiomeAction goes
        // through the same accessor for the same reason.
        Tile tile = ctx.Map.TileInfo;
        if (tile == null)
            return StepOutcome.Fail("Map has no TileInfo to set properties on");

        // Each field is written only when its arg was supplied, so an omitted arg leaves the tile's
        // existing value alone rather than resetting it — see the spec's header.
        if (TryRead(args, SetTilePropertiesStep.ElevationArg, out float elevation))
            tile.elevation = elevation;

        if (TryRead(args, SetTilePropertiesStep.PollutionArg, out float pollution))
            tile.pollution = pollution;

        if (TryRead(args, SetTilePropertiesStep.RainfallArg, out float rainfall))
            tile.rainfall = rainfall;

        // One settle frame, matching SetBiome. Nothing here changes geometry, but anything reading
        // these fields to build a sky colour does so on the next frame's SkyManagerUpdate, and a
        // Screenshot on this frame would capture the pre-change sky.
        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }

    // Re-parses rather than threading a plan struct through, because the spec has already proved
    // every present value parses — so this cannot fail here, and a plan type for three independent
    // optional floats would be more machinery than the step is worth.
    private static bool TryRead(IReadOnlyDictionary<string, string> args, string key, out float value)
    {
        value = 0f;
        return args.TryGetValue(key, out string? raw)
               && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
