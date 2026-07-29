using System.Collections.Generic;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of SetSnow. See Shared/Steps/BuiltIn/SetSnowStep.cs for the pure half and
// the rationale; together they are the whole step.
//
// Thin by construction, exactly like SetTerrainAction: the planner in Shared has already parsed,
// defaulted and range-checked everything, so this is only the seam between that plan and the grid.
public sealed class SetSnowAction : IStepAction
{
    public string Type => SetSnowStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SetSnowStep.TryPlan(args, out SnowPlan plan, out string? error))
            return StepOutcome.Fail($"SetSnow: {error}");

        string? snowError = SceneBuilder.LaySnow(ctx.Map, plan);
        if (snowError != null)
            return StepOutcome.Fail($"SetSnow: {snowError}");

        // Settles like the other scene steps: snow changes what the ground reflects, and a
        // Screenshot on the same frame would capture the pre-snow mesh.
        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}
