using System;
using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// Parallel to FeatureRegistry and ProbeRegistry, and for the same one-directional reason: the harness
// must never take a compile-time reference to a mod under test. Here the mod registers a callback
// saying "I cache something derived from the world state you override — call this when you change
// it", and the harness fires it without knowing what is being invalidated.
//
// Why this exists. The harness's SetTile does not move the colony; it sets
// HarnessRuntime.ForcedLatitude and lets Patch_ForcedLatitude rewrite what every WorldGrid.LongLatOf
// caller sees. That is cheap and reversible, but it means the map's TILE never changes — so a mod
// caching anything per-tile has no way to notice. CelestialLighting's SunClock is exactly that: a
// half-day cache keyed by tileId, re-measured only when the absolute day rolls over, so a scenario at
// latitude 45 could read a half-day measured for its predecessor at latitude 20 and report a
// confidently wrong sun_elevation. Its own SetSeason sometimes hid this by rolling the day index,
// making the bug depend on step ordering — worse than one that never works.
//
// Lives in Shared, with no game types, for the reason ScenarioResidue gives: the decision is then
// unit-testable offline instead of only checkable by booting RimWorld.
public static class WorldOverrideHookRegistry
{
    private static readonly List<Action> Hooks = new();

    // A list rather than a name-keyed dictionary, unlike the other two registries: nothing addresses
    // these individually — no scenario step fires one hook by name, and the whole set always runs
    // together. Registering the same callback twice runs it twice, which for a cache flush is
    // harmless.
    public static void Register(Action onWorldOverrideChanged) => Hooks.Add(onWorldOverrideChanged);

    public static int Count => Hooks.Count;

    // Where a throwing hook is reported. Left as a sink the Mod assembly assigns (to Verse.Log.Warning)
    // rather than a direct Log call, because that is the single line that would drag Verse into this
    // file and cost the offline testability the class is placed in Shared to get. Null in tests, and
    // in any host that has not wired one up.
    public static Action<string>? ErrorSink { get; set; }

    // Only used by tests, which would otherwise inherit whatever registrations an earlier test made —
    // static state in a test assembly is shared across the whole run.
    public static void ClearForTesting()
    {
        Hooks.Clear();
        ErrorSink = null;
    }

    // Exceptions are caught per hook rather than allowed to escape. The caller is a property setter on
    // a path that runs mid-scenario and, in live companion mode, inside a real player's game — a mod's
    // cache flush throwing there would abort whatever step was mid-flight and present as a harness
    // failure. A hook that throws has failed to invalidate, so the worst case is the stale reading
    // this class exists to prevent: no worse than never having registered, and the remaining hooks
    // still run.
    public static void FireAll()
    {
        for (int i = 0; i < Hooks.Count; i++)
        {
            try
            {
                Hooks[i]();
            }
            catch (Exception ex)
            {
                ErrorSink?.Invoke($"world-override hook threw, a mod cache may be stale: {ex.Message}");
            }
        }
    }
}
