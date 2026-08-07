using System.Collections.Generic;
using RimWorldTestHarness.Shared.Steps.BuiltIn;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of SetSand. See Shared/Steps/BuiltIn/SetSandStep.cs for the pure half and
// the rationale; together they are the whole step.
//
// Thin by construction, exactly like SetSnowAction: the planner in Shared has already parsed,
// defaulted and range-checked everything, so this is only the seam between that plan and the grid.
public sealed class SetSandAction : IStepAction
{
    public string Type => SetSandStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        // SKIP, not fail — same call as LandInOrbitAction's. Map.sandGrid is only constructed when
        // ModsConfig.OdysseyActive (sand is Odyssey content), so a box without the DLC cannot be made
        // to run this scenario by fixing any mod, and failing here would paint a permanent red on an
        // otherwise healthy install. The driver abandons the rest of the scenario and reports it as
        // skipped.
        if (ctx.Map.sandGrid == null)
        {
            return StepOutcome.Skip(
                $"{SetSandStep.StepType} needs the Odyssey DLC (sand is its content) and it is not " +
                "active in this run's mod list");
        }

        if (!SetSandStep.TryPlan(args, out SandPlan plan, out string? error))
            return StepOutcome.Fail($"SetSand: {error}");

        string? sandError = SceneBuilder.LaySand(ctx.Map, plan);
        if (sandError != null)
            return StepOutcome.Fail($"SetSand: {sandError}");

        // Settles like the other scene steps: sand changes what the ground reflects, and a
        // Screenshot on the same frame would capture the pre-sand mesh.
        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}
