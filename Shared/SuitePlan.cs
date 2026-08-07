using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// How hard the suite should work to isolate one scenario from the next. Chosen by the caller
// (RWTH_ISOLATION / run_test.sh --isolation) rather than hardcoded, because the right answer depends
// on what the suite contains and the cheap answer is also the dangerous one.
public enum IsolationPolicy
{
    // The default. Reload the save only where a soft reset provably cannot restore the world, i.e.
    // after a scenario that mutated the map. Clean suites pay nothing; map-mutating ones pay one
    // reload each.
    Auto,

    // Reload before every scenario after the first. Slower, and the answer to "I don't trust the
    // residue analysis" — e.g. while bisecting a scenario that passes alone and fails in a suite.
    Always,

    // Never reload; soft-reset only. An explicit assertion that the suite's scenarios tolerate a
    // shared world. Where that is provably false the planner records a note (not an error), because
    // the caller opted in — unlike Auto, where the same situation is an involuntary degradation.
    Never,
}

// What the driver does immediately before a scenario starts.
public enum IsolationAction
{
    // Nothing needed: either this is the first scenario (the fresh load IS the isolation) or nothing
    // ran since the last full reset that left anything behind.
    None,

    // Restore the handful of values we know how to restore (see Mod/WorldStateReset.cs).
    SoftReset,

    // Reload the save the run booted from, mid-session, via GameDataSaveLoader.LoadGame.
    ReloadSave,
}

public sealed class SuiteEntryPlan
{
    public int ScenarioIndex { get; set; }
    public string ScenarioName { get; set; } = "";

    // Isolation to apply BEFORE this scenario's first step.
    public IsolationAction Before { get; set; }

    // What this scenario will leave behind, which is what the NEXT entry's Before was decided from.
    public ScenarioResidue Residue { get; set; }
}

public sealed class SuitePlan
{
    public List<SuiteEntryPlan> Entries { get; set; } = new();

    // Suite-level problems. Non-empty means the suite cannot pass (ReportComparer.AllPass), because
    // every error here describes a way the run would verify less than it claims to.
    public List<string> Errors { get; set; } = new();

    // Isolation shortfalls the caller explicitly asked for. Recorded and reported, but not failing —
    // the distinction from Errors is consent: a note is something the caller chose, an error is
    // something the environment imposed.
    public List<string> Notes { get; set; } = new();
}

// Decides, for an ordered list of scenarios, what has to happen between each pair. Pure: no game, no
// filesystem, no clock — which is the point, because this is the part of scenario batching that is
// easy to get subtly wrong and impossible to check by looking at a green run.
//
// Scenario ORDER is never changed. Reordering to group map-mutating scenarios together would cut
// reloads, but it would also mean the suite that ran isn't the suite that was written down, and a
// scenario's result would depend on which other scenarios happened to be in the run. Paying for an
// extra reload is much cheaper than that ambiguity.
public static class SuitePlanner
{
    public const string PolicyAuto = "auto";
    public const string PolicyAlways = "always";
    public const string PolicyNever = "never";

    // The trivial case, kept explicitly separate from Plan: a single-scenario run is exactly what the
    // harness did before suites existed, so it gets no boundaries, no residue analysis and no suite
    // gates — nothing that could change its behaviour.
    public static SuitePlan Single(ScenarioSpec scenario)
    {
        SuitePlan plan = new SuitePlan();
        plan.Entries.Add(new SuiteEntryPlan
        {
            ScenarioIndex = 0,
            ScenarioName = scenario.Name,
            Before = IsolationAction.None,
            Residue = ScenarioResidueAnalyzer.OfScenario(scenario),
        });
        return plan;
    }

    // reloadAvailable is false when the run has no save to reload — the -quicktest path generates its
    // colony at boot and never writes one. Passed in rather than inferred so this stays pure and so
    // the caller (HarnessMod, from RWTH_RELOAD_SAVE) owns the "is there a save?" question.
    //
    // profilerAlreadyActive is the run-level profiling mode (see Shared/RunProfiling.cs). When it is
    // true the analyzer was started once, after the first map load and before the first scenario's
    // first step, so every scenario in the suite is instrumented identically from before any of them
    // ran — and ScenarioResidue.Profiler stops describing anything one scenario can do to the next.
    // Masking it here is what keeps a suite of profiling scenarios at ONE boot instead of one reload
    // per scenario, which was the entire reason run-level profiling replaced the opt-in step.
    public static SuitePlan Plan(IReadOnlyList<ScenarioSpec> scenarios, IsolationPolicy policy,
                                 bool reloadAvailable, bool profilerAlreadyActive = false)
    {
        SuitePlan plan = new SuitePlan();
        if (scenarios.Count == 0)
        {
            // Same vacuous-truth hazard as an empty probe-check list: an empty suite must not pass.
            plan.Errors.Add("suite contains no scenarios");
            return plan;
        }

        plan.Errors.AddRange(SuiteScreenshots.CollisionErrors(scenarios));
        if (policy == IsolationPolicy.Always && !reloadAvailable)
        {
            plan.Errors.Add(
                "isolation=always was requested but this run has no save to reload " +
                "(quicktest mode generates its colony at boot) — use a fixture, or isolation=auto/never");
        }

        // Residue accumulated by every scenario since the last thing that cleared it. Not just the
        // previous scenario's: a soft reset leaves Map residue in place, so scenario 4 must still
        // reload if scenario 1 spawned things and 2-3 only moved the clock.
        ScenarioResidue pending = ScenarioResidue.None;

        for (int i = 0; i < scenarios.Count; i++)
        {
            IsolationAction before = i == 0
                ? IsolationAction.None
                : ActionFor(pending, policy, reloadAvailable);

            RecordShortfall(plan, scenarios[i].Name, pending, before, policy);
            pending = ResidueAfter(pending, before);

            ScenarioResidue left = ResidueOf(scenarios[i], profilerAlreadyActive);
            plan.Entries.Add(new SuiteEntryPlan
            {
                ScenarioIndex = i,
                ScenarioName = scenarios[i].Name,
                Before = before,
                Residue = left,
            });

            pending |= left;
        }

        return plan;
    }

    // A scenario's residue as this run experiences it. The only thing subtracted is Profiler, and only
    // when the run started the analyzer before the suite did anything: residue means "what this
    // scenario leaves behind for the NEXT one", and a profiler that was already running when scenario 1
    // began is not something scenario 3 did to scenario 4. Nothing else is ever masked — every other
    // flag describes a change a scenario genuinely made.
    private static ScenarioResidue ResidueOf(ScenarioSpec scenario, bool profilerAlreadyActive)
    {
        ScenarioResidue residue = ScenarioResidueAnalyzer.OfScenario(scenario);
        return profilerAlreadyActive ? residue & ~ScenarioResidue.Profiler : residue;
    }

    private static IsolationAction ActionFor(ScenarioResidue pending, IsolationPolicy policy, bool reloadAvailable)
    {
        if (policy == IsolationPolicy.Always && reloadAvailable)
            return IsolationAction.ReloadSave;

        if (policy != IsolationPolicy.Never && reloadAvailable && MapDirty(pending))
            return IsolationAction.ReloadSave;

        return pending == ScenarioResidue.None ? IsolationAction.None : IsolationAction.SoftReset;
    }

    private static ScenarioResidue ResidueAfter(ScenarioResidue pending, IsolationAction action)
    {
        if (action == IsolationAction.ReloadSave)
            return ScenarioResidue.None;
        if (action == IsolationAction.SoftReset)
            return pending & ScenarioResidueAnalyzer.RequiresReload;

        return pending;
    }

    // The one case where the harness knowingly runs a scenario against a world another scenario
    // changed. It must never be silent: either the caller asked for it (a note) or the environment
    // couldn't do better (an error, which fails the suite).
    private static void RecordShortfall(SuitePlan plan, string scenarioName, ScenarioResidue pending,
                                       IsolationAction chosen, IsolationPolicy policy)
    {
        if (!MapDirty(pending) || chosen == IsolationAction.ReloadSave)
            return;

        string detail =
            $"scenario '{scenarioName}' runs after an earlier scenario in this suite mutated the map " +
            "(PlaceThings/SetTerrain spawn, repaint and unfog irreversibly), which a soft reset cannot undo";

        if (policy == IsolationPolicy.Never)
        {
            plan.Notes.Add($"{detail} — isolation=never, so it runs against the mutated world as requested");
            return;
        }

        plan.Errors.Add(
            $"{detail} — and this run has no save to reload, so it cannot be isolated. " +
            "Run it against a fixture save, split the suite, or pass isolation=never to accept the shared world");
    }

    private static bool MapDirty(ScenarioResidue pending) => (pending & ScenarioResidue.Map) != 0;

    // Absent/empty means the default rather than an error, so callers can pass an unset env var
    // straight through. An unrecognised value is rejected rather than defaulted: silently falling back
    // to Auto on "alwyas" would produce a run that isolated less than asked, and look identical to one
    // that did.
    public static bool TryParsePolicy(string? raw, out IsolationPolicy policy, out string? error)
    {
        error = null;
        policy = IsolationPolicy.Auto;
        if (string.IsNullOrEmpty(raw))
            return true;

        switch (raw!.Trim().ToLowerInvariant())
        {
            case PolicyAuto:
                policy = IsolationPolicy.Auto;
                return true;
            case PolicyAlways:
                policy = IsolationPolicy.Always;
                return true;
            case PolicyNever:
                policy = IsolationPolicy.Never;
                return true;
            default:
                error = $"unknown isolation policy '{raw}' (expected {PolicyAuto}, {PolicyAlways} or {PolicyNever})";
                return false;
        }
    }
}
