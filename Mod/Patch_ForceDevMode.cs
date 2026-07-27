using HarmonyLib;
using Verse;

namespace RimWorldTestHarness.Mod;

// Forces Prefs.DevMode true for the duration of a batch scenario so DevMode-gated vanilla behaviour
// (alert/tutor suppression, dev-only debug actions) is consistent during a run without touching the
// user's real Prefs.xml. Normal play (RWTH_SCENARIO unset) is completely unaffected.
//
// NOT part of the autostart-save chain, despite what this comment used to claim. Loading the fixture
// needs Prefs.DevMode true at Root_Entry.Start()'s autostart check, which runs synchronously on the
// line after base.Start() returns — whereas Root.Start() only QUEUES PlayDataLoader.LoadAllPlayData()
// as an async long event, and both LoadedModManager.LoadAllActiveMods() and
// StaticConstructorOnStartupUtility.CallAll() live inside it. This patch is therefore not applied yet
// when vanilla makes that check, and never could be — runs that "worked" were relying on the user's
// ambient devMode being true. Runner/run_test.sh seeds <devMode>True</devMode> into Prefs.xml and
// restores it on teardown, because that is the only place it can be set in time.
// See DESIGN.md, "Save loading: the vanilla autostart mechanism".
[HarmonyPatch(typeof(Prefs), nameof(Prefs.DevMode), MethodType.Getter)]
public static class Patch_ForceDevMode
{
    static void Postfix(ref bool __result)
    {
        // HarnessRuntime.ForceDevMode is set only by the BATCH ScenarioDriver, never by the live
        // companion — the companion runs against the user's real game and must not flip DevMode.
        if (HarnessRuntime.ForceDevMode)
            __result = true;
    }
}
