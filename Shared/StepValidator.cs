using System.Collections.Generic;
using RimWorldTestHarness.Shared.Steps;

namespace RimWorldTestHarness.Shared;

// Load-time checks over an already-desugared step list, run at ScenarioSpecLoader's single choke
// point so every consumer gets them.
//
// Two jobs, both about failing on the spec rather than on the run:
//
//   * Unknown step types. A typo'd type would otherwise surface only as an executed-step error deep
//     into a run.
//   * Per-step arg checks. A spec's TryValidate is pure, so it can be dry-run here without a Map.
//     The scene steps are the main beneficiaries — a bad `cols` is reported before the game spends
//     minutes producing frames of an empty scene, the same argument TimelapseExpander makes for
//     validating `fps` at load time even though no step consumes it.
//
// Both answers now come from the step's own registered spec instead of lists maintained here, which
// is what lets a contributor add a step without editing this file. Errors land in
// ScenarioSpec.LoadErrors, which ScenarioDriver.Begin folds into the run's report. The steps are
// deliberately left in place: StepExecutor fails them again at execution, and a scenario that
// quietly dropped a step would verify less than it claims to.
public static class StepValidator
{
    public static void ValidateAll(IReadOnlyList<ScenarioStep> steps, List<string> errors)
    {
        for (int i = 0; i < steps.Count; i++)
            Validate(steps[i], i, errors);
    }

    // The step index is included in every message because a scenario's steps have no names, and a
    // full-day Timelapse expands to hundreds of them — "step 3" is the only way to point at one.
    private static void Validate(ScenarioStep step, int index, List<string> errors)
    {
        // A Timelapse reaching here failed to expand, which already produced a LoadError with the
        // specific reason. It stays registered (see TimelapseStep) so this pass doesn't pile a
        // redundant "unknown type" on top of it.
        if (!StepRegistry.TryGet(step.Type, out IStepSpec? spec) || spec == null)
        {
            errors.Add($"step {index} has unknown type '{step.Type}' (expected one of: " +
                       $"{string.Join(", ", StepRegistry.KnownTypes)})");
            return;
        }

        if (!spec.TryValidate(step.Args, out string? error))
            errors.Add($"step {index} ({step.Type}) is invalid: {error}");
    }
}
