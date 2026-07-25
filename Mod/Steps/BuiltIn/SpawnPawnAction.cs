using System.Collections.Generic;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// Spawns pawns into the scene. Like PlaceThings, the layout arithmetic and arg validation live in the
// pure Shared/PawnLayout.cs; SceneBuilder.SpawnPawns is the Verse-touching adapter that resolves the
// PawnKindDef/faction/hediffs and calls PawnGenerator/GenSpawn. This class is just the seam.
//
// Returns WaitFrames = SceneSettleFrames for the same reason PlaceThings does: a freshly spawned pawn
// needs a frame for its graphic to render before a following Screenshot captures it, otherwise the
// image can miss the pawn that the step reported as spawned.
public sealed class SpawnPawnAction : IStepAction
{
    public string Type => StepArgs.SpawnPawnType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!PawnLayout.TryPlan(args, out PawnPlan plan, out string? error))
            return StepOutcome.Fail($"SpawnPawn: {error}");

        string? spawnError = SceneBuilder.SpawnPawns(ctx.Map, plan);
        if (spawnError != null)
            return StepOutcome.Fail($"SpawnPawn: {spawnError}");

        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}
