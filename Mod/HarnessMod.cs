using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod;

// Dev-only, opt-in: this mod does nothing in normal play. It only activates when the Runner
// launches RimWorld with RWTH_SCENARIO or RWTH_SUITE set, so accidentally leaving this mod enabled in
// a player's normal mod list is harmless.
[StaticConstructorOnStartup]
public static class HarnessMod
{
    // One scenario, one boot, bare ScenarioReport on disk — the original mode, still the fallback.
    private const string EnvScenario = "RWTH_SCENARIO";

    // Path to a suite list file (see Shared/SuiteList.cs): several scenarios in ONE boot, SuiteReport
    // on disk. Mutually exclusive with RWTH_SCENARIO and takes precedence if both are set, because a
    // suite is the more specific request.
    private const string EnvSuite = "RWTH_SUITE";

    private const string EnvReport = "RWTH_REPORT";

    // auto | always | never — see Shared/SuitePlan.cs. Suite runs only.
    private const string EnvIsolation = "RWTH_ISOLATION";

    // Save name (no extension, no path) the driver reloads between scenarios that need real isolation.
    // Runner/run_test.sh sets it to "autostart" in fixture mode and leaves it UNSET in -quicktest mode,
    // where the colony is generated at boot and no save exists — the planner then refuses to pretend a
    // map-mutating suite was isolated. Suite runs only.
    private const string EnvReloadSave = "RWTH_RELOAD_SAVE";

    static HarnessMod()
    {
        new Harmony("joof.rimworldtestharness").PatchAll();

        // The one line that would otherwise force WorldOverrideHookRegistry to reference Verse and so
        // cost it the offline testability it sits in Shared to get. Wired here rather than lazily so a
        // throwing hook is never silently dropped in a real run; in tests the sink stays null and the
        // throw is swallowed, which is the documented behaviour.
        Shared.WorldOverrideHookRegistry.ErrorSink = message => Log.Warning($"[RWTH] {message}");

        // The harness's own clock, readable like any other probe. Registered here rather than left to
        // a mod under test because what they measure is this harness's time manipulation, not any
        // mod's behaviour — and a scenario debugging a clock problem should not have to depend on
        // whichever mod happens to be loaded to ask what time it is.
        Probes.ProbeRegistry.Register(new Probes.BuiltIn.TicksAbsProbe());
        Probes.ProbeRegistry.Register(new Probes.BuiltIn.AbsoluteDayProbe());

        // Before anything can load a scenario: the loader validates step types against the registry,
        // so discovery has to have run or every step in every scenario reads as an unknown type.
        Steps.StepDiscovery.RunOnce();

        // Unconditional marker (scenario or not): Runner/run_test.sh's crash-retry logic greps
        // Player.log for this to tell "died before our mod loaded — flaky early-startup crash,
        // retry" from "died after — real crash, surface it".
        Log.Message("RWTH: harness loaded");

        string? suitePath = Read(EnvSuite);
        string? scenarioPath = Read(EnvScenario);
        if (suitePath == null && scenarioPath == null)
            return;

        string? reportPath = Read(EnvReport);
        if (reportPath == null)
        {
            Log.Error($"[RimWorldTestHarness] {EnvScenario}/{EnvSuite} set but {EnvReport} is not — nothing will run.");
            return;
        }

        // Both Begin* calls set ScenarioDriver.Active synchronously, before any scene has loaded —
        // that's what makes Patch_ForceDevMode active in time for Verse.Root_Entry.Start()'s
        // autostart-save check, which runs later in the same boot sequence. See DESIGN.md for why that
        // ordering matters.
        if (suitePath != null)
            BeginSuite(suitePath, reportPath);
        else
            ScenarioDriver.Begin(ScenarioSpecLoader.LoadFromFile(scenarioPath!), reportPath);
    }

    private static void BeginSuite(string suitePath, string reportPath)
    {
        List<string> launchErrors = new List<string>();

        SuiteSpec suite = ScenarioSpecLoader.LoadSuiteFromFile(suitePath);
        launchErrors.AddRange(suite.LoadErrors);

        // A malformed policy is an error rather than a silent fall back to the default: a run that
        // isolated less than it was asked to looks exactly like one that isolated correctly.
        if (!SuitePlanner.TryParsePolicy(Read(EnvIsolation), out IsolationPolicy policy, out string? policyError))
            launchErrors.Add($"{EnvIsolation}: {policyError}");

        string? reloadSaveName = Read(EnvReloadSave);
        SuitePlan plan = SuitePlanner.Plan(suite.Scenarios, policy, reloadAvailable: reloadSaveName != null);

        Log.Message($"RWTH: suite of {suite.Scenarios.Count} scenario(s), isolation={policy}, " +
                    $"reloadSave={reloadSaveName ?? "(none)"}");

        ScenarioDriver.BeginSuite(suite.Scenarios, plan, reportPath, reloadSaveName, launchErrors);
    }

    // Treats empty exactly like unset, so an env var that a shell expanded to nothing doesn't put the
    // harness into a mode with a blank path in it.
    private static string? Read(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
