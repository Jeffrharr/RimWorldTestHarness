using System.Collections.Generic;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// Scene setup: put deliberate, known-position geometry on the map so a lighting screenshot has
// something to actually show. All the layout arithmetic and arg validation is in the pure
// Shared/SceneLayout.cs; SceneBuilder is the Verse-touching adapter. These three are just the seam
// between them.
//
// PlaceThings and SetTerrain return WaitFrames = SceneSettleFrames so a following Screenshot can't
// capture the frame before the glow grid and shadow direction have taken the new geometry into
// account — the same staleness hazard the Wait step exists for, but built in rather than left to the
// scenario author, because forgetting it produces a plausible-looking wrong image.

public sealed class PlaceThingsAction : IStepAction
{
    public string Type => StepArgs.PlaceThingsType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SceneLayout.TryPlan(args, out ScenePlan plan, out string? error))
            return StepOutcome.Fail($"PlaceThings: {error}");

        string? buildError = SceneBuilder.Build(ctx.Map, plan);
        if (buildError != null)
            return StepOutcome.Fail($"PlaceThings: {buildError}");

        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}

public sealed class SetTerrainAction : IStepAction
{
    public string Type => StepArgs.SetTerrainType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SceneLayout.TryPlanTerrain(args, out TerrainPlan plan, out string? error))
            return StepOutcome.Fail($"SetTerrain: {error}");

        string? paintError = SceneBuilder.PaintTerrain(ctx.Map, plan);
        if (paintError != null)
            return StepOutcome.Fail($"SetTerrain: {paintError}");

        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}

// Settles like its siblings, and needs it more than either: roof changes dirty the map mesh, and
// the lighting/shadow layers that read the roof grid only rebuild on the regenerate that follows.
public sealed class SetRoofAction : IStepAction
{
    public string Type => StepArgs.SetRoofType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SceneLayout.TryPlanRoof(args, out RoofPlan plan, out string? error))
            return StepOutcome.Fail($"SetRoof: {error}");

        string? paintError = SceneBuilder.PaintRoof(ctx.Map, plan);
        if (paintError != null)
            return StepOutcome.Fail($"SetRoof: {paintError}");

        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}

// No settle wait: the camera jump is instant and changes nothing the lighting depends on.
public sealed class LookAtAction : IStepAction
{
    public string Type => StepArgs.LookAtType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SceneLayout.TryPlanLookAt(args, out LookAtPlan plan, out string? error))
            return StepOutcome.Fail($"LookAt: {error}");

        string? lookError = SceneBuilder.LookAt(ctx.Map, plan);
        if (lookError != null)
            return StepOutcome.Fail($"LookAt: {lookError}");

        return new StepOutcome();
    }
}
