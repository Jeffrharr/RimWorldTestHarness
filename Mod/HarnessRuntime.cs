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
    // Whether the run stays open once its last scenario finishes, instead of quitting the game.
    //
    // BATCH-ONLY, AND DELIBERATELY NOT "is a driver active" EITHER. It exists for hands-on inspection:
    // a scenario builds a specific world state — a season, an hour, a weather, a camera — that would
    // take a person several minutes of dev-menu poking to reproduce, and then the run throws it away
    // by quitting. With this set the driver finishes normally, writes its report, hands the UI back
    // and leaves the game running so somebody can look at what the scenario built and play on from
    // it.
    //
    // The live companion driver never sets this: it is already attached to a game nobody was going to
    // quit, so there is nothing for it to hold open.
    public static bool HoldOpen { get; set; }

    // Whether DialogGuard stands down and leaves blocking modals on screen. run_test.sh
    // --keep-dialogs; see DialogGuard for what it changes, and README for when to reach for it.
    //
    // BATCH-ONLY, like HoldOpen above, and for the same reason: it is a statement of the runner's
    // intent for this run, not something a scenario decides. The live companion driver never sets it
    // because it never suppresses anything in the first place — the guard is gated on a batch
    // scenario being Active, so a normally-launched game already keeps all of its dialogs.
    //
    // It is a DEBUGGING flag and it can strand a run: the whole point of the guard is that a
    // forcePause modal stops TicksGame, so a scenario left holding one sits in FastForward until the
    // runner's timeout. That is why the guard still classifies and REPORTS every window it leaves up
    // rather than silently obeying — an unexplained stall is the exact failure the guard was written
    // to stop happening.
    public static bool KeepDialogs { get; set; }

    // Batch scenarios set this true for the duration of a run so Prefs.DevMode reads true and the
    // fixture autostart-save loads. Live companion mode leaves it false.
    public static bool ForceDevMode { get; set; }

    // Overrides the latitude every WorldGrid.LongLatOf caller sees. Null = no override (real tile
    // latitude). Set by a SetTile action; longitude is intentionally left real (see
    // Patch_ForcedLatitude).
    //
    // Hand-written rather than auto-implemented so a change can fire WorldOverrideHookRegistry: a mod
    // under test may cache something derived from latitude, and because SetTile overrides the reading
    // rather than moving the colony, a per-TILE cache has no other way to learn its answer just went
    // stale. See WorldOverrideHookRegistry for the case that motivated it.
    public static float? ForcedLatitude
    {
        get => forcedLatitude;
        set
        {
            // Only fire on an actual change. WorldStateReset nulls this between every scenario in a
            // suite, including the many that never set it, so firing on a no-op write would scale the
            // hook's cost with scenario count rather than with latitude changes.
            if (NullableFloatEquals(forcedLatitude, value))
                return;

            forcedLatitude = value;
            Shared.WorldOverrideHookRegistry.FireAll();
        }
    }

    private static float? forcedLatitude;

    // Named rather than inlined into the setter's condition: nullable-float equality has three cases
    // (both null, one null, both present) and spelling them out inside an `if` is the kind of thing a
    // reader has to hold in their head while checking the setter does what its comment claims.
    private static bool NullableFloatEquals(float? a, float? b)
    {
        if (!a.HasValue && !b.HasValue)
            return true;
        if (!a.HasValue || !b.HasValue)
            return false;
        return a.Value.Equals(b.Value);
    }
}
