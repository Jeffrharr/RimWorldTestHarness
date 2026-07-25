using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// Load-time checks over an already-desugared step list, run at ScenarioSpecLoader's single choke
// point so every consumer gets them.
//
// Two jobs, both about failing on the spec rather than on the run:
//
//   * Unknown step types. ScenarioSpec.cs has always documented that "ScenarioSpecLoader validates
//     known Type values"; until now nothing did, and a typo'd type surfaced only as an executed-step
//     error deep into a run.
//   * Scene-setup args. SceneLayout's planners are pure, so they can be dry-run here without a Map.
//     A bad `cols` is then reported before the game spends minutes producing frames of an empty
//     scene — the same argument TimelapseExpander makes for validating `fps` at load time even
//     though no step consumes it.
//
// Errors land in ScenarioSpec.LoadErrors, which ScenarioDriver.Begin folds into the run's report.
// The steps are deliberately left in place: StepExecutor fails them again at execution, and a
// scenario that quietly dropped a step would verify less than it claims to.
public static class StepValidator
{
    private static readonly string[] KnownStepTypes =
    {
        StepArgs.SetTileType,
        StepArgs.SetSeasonType,
        StepArgs.SetTimeType,
        StepArgs.FastForwardType,
        StepArgs.ProbeType,
        StepArgs.ScreenshotType,
        StepArgs.SetFeatureType,
        StepArgs.WaitType,
        StepArgs.StartConditionType,
        StepArgs.PlaceThingsType,
        StepArgs.SetTerrainType,
        StepArgs.LookAtType,
        // Timelapse is expanded away by TimelapseExpander before this runs, so reaching here means
        // its own args failed to validate. That already produced a LoadError with the specific
        // reason; listing it keeps this pass from piling a redundant "unknown type" on top.
        StepArgs.TimelapseType,
    };

    public static void ValidateAll(IReadOnlyList<ScenarioStep> steps, List<string> errors)
    {
        for (int i = 0; i < steps.Count; i++)
            Validate(steps[i], i, errors);
    }

    // The step index is included in every message because a scenario's steps have no names, and a
    // full-day Timelapse expands to hundreds of them — "step 3" is the only way to point at one.
    private static void Validate(ScenarioStep step, int index, List<string> errors)
    {
        if (System.Array.IndexOf(KnownStepTypes, step.Type) < 0)
        {
            errors.Add($"step {index} has unknown type '{step.Type}' (expected one of: " +
                       $"{string.Join(", ", KnownStepTypes)})");
            return;
        }

        if (!TryValidateScene(step, out string? error))
            errors.Add($"step {index} ({step.Type}) is invalid: {error}");
    }

    private static bool TryValidateScene(ScenarioStep step, out string? error)
    {
        switch (step.Type)
        {
            case StepArgs.PlaceThingsType:
                return SceneLayout.TryPlan(step.Args, out _, out error);
            case StepArgs.SetTerrainType:
                return SceneLayout.TryPlanTerrain(step.Args, out _, out error);
            case StepArgs.LookAtType:
                return SceneLayout.TryPlanLookAt(step.Args, out _, out error);
            default:
                error = null;
                return true;
        }
    }
}
