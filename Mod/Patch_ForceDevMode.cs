using HarmonyLib;
using Verse;

namespace RimWorldTestHarness.Mod;

// SaveGameFilesUtility.GetAutostartSaveFile() (called from vanilla's own Root_Entry.Start()) only
// looks for an "autostart" save when Prefs.DevMode is true — that's the entire mechanism this
// harness relies on to load a fixture without any custom LoadGame call of its own. Forcing DevMode
// true only while a scenario is active means a scenario run never has to touch the user's real
// Prefs.xml, and normal play (RWTH_SCENARIO unset) is completely unaffected.
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
