using System.Collections.Generic;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// Spawns wild animals into the scene. Like PlaceThings, the layout arithmetic and arg validation live
// in the pure Shared/AnimalLayout.cs; SceneBuilder.SpawnAnimals is the Verse-touching adapter that
// resolves the PawnKindDef and calls PawnGenerator/GenSpawn. This class is just the seam.
//
// Returns WaitFrames = SceneSettleFrames for the same reason PlaceThings does: a freshly spawned pawn
// needs a frame for its graphic to render before a following Screenshot captures it, otherwise the
// image can miss the animal that the step reported as spawned.
public sealed class SpawnAnimalAction : IStepAction
{
    public string Type => StepArgs.SpawnAnimalType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!AnimalLayout.TryPlan(args, out AnimalPlan plan, out string? error))
            return StepOutcome.Fail($"SpawnAnimal: {error}");

        string? spawnError = SceneBuilder.SpawnAnimals(ctx.Map, plan);
        if (spawnError != null)
            return StepOutcome.Fail($"SpawnAnimal: {spawnError}");

        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}
