using HarmonyLib;
using Verse;

namespace RimWorldTestHarness.Mod;

// Stops a blocking modal from ever reaching the window stack while a scenario is running.
//
// WHY A PREFIX ON Add RATHER THAN CLEARING THE STACK AFTERWARDS. Dialog_GiveName sets
// `forcePause = true`, and that takes effect the moment the window is added. A per-frame sweep can
// only remove it on the NEXT frame, which is one frame of a stopped tick clock — usually harmless,
// but it makes the fix racy in exactly the situation it exists for, and it means the dialog is on
// screen for any Screenshot step that happens to fire in between. Refusing the Add is the version
// with no window of vulnerability at all: the dialog is never on the stack, so it never pauses
// anything and never appears in a frame.
//
// DialogGuard.ClearBlockingDialogs still runs from ScenarioDriver.Tick as a backstop, for windows
// that were added before the scenario went Active (during load, or by another mod's startup) and are
// therefore already up by the time this patch starts refusing them.
//
// Gated on ScenarioDriver.Active so this is inert in ordinary play and in the live-command driver —
// a harness install must not silently eat the player's dialogs when no scenario is running.
[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
public static class Patch_SuppressBlockingWindows
{
    // Returning false skips the original Add, so the window is never stacked. Note the dialog object
    // is still constructed by its caller either way — which is what makes resolving it here possible,
    // because Dialog_NamePlayerFactionAndSettlement generates and validates both names in its own
    // constructor (see DialogGuard.ResolveGiveName).
    static bool Prefix(Window window)
    {
        if (!ScenarioDriver.Active)
            return true;

        return !DialogGuard.TrySuppress(window);
    }
}
