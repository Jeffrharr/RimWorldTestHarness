using HarmonyLib;
using Verse;

namespace RimWorldTestHarness.Mod;

// Root_Play.Update() is the natural once-per-frame hook once a game is loaded and playing — it's
// what vanilla itself uses to drive UpdatePlay(), music, etc. A postfix here is a no-op in normal
// play (ScenarioDriver.Tick() bails immediately when no scenario is Active), so this patch is safe
// to have applied unconditionally.
[HarmonyPatch(typeof(Root_Play), nameof(Root_Play.Update))]
public static class Patch_DriveScenario
{
    static void Postfix() => ScenarioDriver.Tick();
}
