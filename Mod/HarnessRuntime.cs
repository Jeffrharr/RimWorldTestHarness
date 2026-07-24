namespace RimWorldTestHarness.Mod;

// Shared runtime flags read by the generic Harmony postfixes (Patch_ForceDevMode /
// Patch_ForcedLatitude). Pulled out of ScenarioDriver so BOTH drivers — the batch ScenarioDriver
// and the interactive LiveCommandDriver — can feed the same patches without either one depending on
// the other.
//
// The two flags are deliberately SEPARATE (not one "is a driver active" bool), because batch mode
// and companion mode want opposite things from them:
//
//   * ForceDevMode is a batch-only hack: forcing Prefs.DevMode true is what makes vanilla's
//     autostart-save mechanism load the fixture (see Patch_ForceDevMode / DESIGN.md). The live
//     companion runs against the user's REAL, normally-launched game, so it must never flip DevMode
//     on — that would change the player's HUD/behaviour out from under them.
//
//   * ForcedLatitude is set only by an explicit SetTile action, by whichever driver ran it. It's a
//     deliberate, opt-in state change, safe in either mode.
public static class HarnessRuntime
{
    // Batch scenarios set this true for the duration of a run so Prefs.DevMode reads true and the
    // fixture autostart-save loads. Live companion mode leaves it false.
    public static bool ForceDevMode { get; set; }

    // Overrides the latitude every WorldGrid.LongLatOf caller sees. Null = no override (real tile
    // latitude). Set by a SetTile action; longitude is intentionally left real (see
    // Patch_ForcedLatitude).
    public static float? ForcedLatitude { get; set; }
}
