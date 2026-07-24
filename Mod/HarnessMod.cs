using System;
using HarmonyLib;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod;

// Dev-only, opt-in: this mod does nothing in normal play. It only activates when the Runner
// launches RimWorld with RWTH_SCENARIO set, so accidentally leaving this mod enabled in a
// player's normal mod list is harmless.
[StaticConstructorOnStartup]
public static class HarnessMod
{
    static HarnessMod()
    {
        new Harmony("joof.rimworldtestharness").PatchAll();

        // Unconditional marker (scenario or not): Runner/run_test.sh's crash-retry logic greps
        // Player.log for this to tell "died before our mod loaded — flaky early-startup crash,
        // retry" from "died after — real crash, surface it".
        Log.Message("RWTH: harness loaded");

        string? scenarioPath = Environment.GetEnvironmentVariable("RWTH_SCENARIO");
        if (string.IsNullOrEmpty(scenarioPath))
            return;

        string? reportPath = Environment.GetEnvironmentVariable("RWTH_REPORT");
        if (string.IsNullOrEmpty(reportPath))
        {
            Log.Error("[RimWorldTestHarness] RWTH_SCENARIO set but RWTH_REPORT is not — scenario will not run.");
            return;
        }

        // ScenarioDriver.Begin sets ScenarioDriver.Active synchronously, before any scene has
        // loaded — that's what makes Patch_ForceDevMode active in time for
        // Verse.Root_Entry.Start()'s autostart-save check, which runs later in the same boot
        // sequence. See DESIGN.md for why that ordering matters.
        ScenarioSpec spec = ScenarioSpecLoader.LoadFromFile(scenarioPath);
        ScenarioDriver.Begin(spec, reportPath);
    }
}
