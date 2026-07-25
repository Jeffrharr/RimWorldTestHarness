using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps;

// Desugars composite steps into the primitive ones they stand for, so every consumer downstream of
// the loader — the executor, the residue analyzer, the suite planner — only ever sees primitives and
// needs no knowledge that composites exist.
//
// Registry-driven rather than a Timelapse special case: a composite is now just a step whose spec
// also implements IStepExpander, so a contributor can add one without touching this file. That was
// the last hardcoded step-type check left in the load path.
public static class StepExpansion
{
    // Expands every composite step, passing primitives through untouched.
    //
    // A composite whose args don't validate is LEFT IN PLACE with its reason appended to errors,
    // never dropped. The driver surfaces those in the run's report and the executor fails the
    // leftover step, so a scenario that lost a whole sweep to a typo says so — rather than running
    // short and reporting green, which is the failure mode this repo's validation exists to prevent.
    public static List<ScenarioStep> ExpandAll(IEnumerable<ScenarioStep> steps, List<string> errors)
    {
        List<ScenarioStep> expanded = new List<ScenarioStep>();
        foreach (ScenarioStep step in steps)
            ExpandInto(step, expanded, errors);

        return expanded;
    }

    private static void ExpandInto(ScenarioStep step, List<ScenarioStep> into, List<string> errors)
    {
        if (!TryGetExpander(step.Type, out IStepExpander? expander) || expander == null)
        {
            into.Add(step);
            return;
        }

        int before = errors.Count;
        List<ScenarioStep> produced = new List<ScenarioStep>();
        expander.Expand(step, produced, errors);

        // The expander reporting an error is what marks the expansion as failed — it keeps the
        // "leave the bad step in place" decision here, in one place, rather than trusting every
        // third-party expander to remember to do it.
        if (errors.Count != before)
        {
            into.Add(step);
            return;
        }

        into.AddRange(produced);
    }

    private static bool TryGetExpander(string type, out IStepExpander? expander)
    {
        expander = StepRegistry.TryGet(type, out IStepSpec? spec) ? spec as IStepExpander : null;
        return expander != null;
    }
}
