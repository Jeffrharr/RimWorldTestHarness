using System.Collections.Generic;
using RimWorld;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod.Steps;

// Shared plumbing the built-in actions use, and that a third-party action is welcome to. Extracted
// from StepExecutor's privates when the handlers moved into their own classes.
public static class StepHelpers
{
    // Spawning a building or repainting terrain dirties the glow grid and the shadow map, which
    // vanilla recomputes on a later frame. One frame is enough for geometry (unlike a clock jump,
    // which TimelapseExpander gives two) because nothing here has to propagate through the sky
    // colour chain — raise it if a scene-setup screenshot ever shows the pre-placement lighting.
    public const int SceneSettleFrames = 1;

    // Absent means "use the caller's default" rather than "false", so adding an optional flag to a
    // step never changes what existing scenario files do.
    public static bool ParseBool(IReadOnlyDictionary<string, string> args, string key, bool defaultValue) =>
        args.TryGetValue(key, out string? raw) ? bool.Parse(raw) : defaultValue;

    public static float LongitudeOf(Map map) => Find.WorldGrid.LongLatOf(map.Tile).x;

    // Thin adapter over the pure GameClock math: pull the live inputs off the TickManager/GenDate,
    // compute the target ticksGame (with the never-negative clamp — see GameClock), and set it.
    public static void JumpToLocalTime(int dayOfYear, float hour, float longitude)
    {
        long newTicksGame = GameClock.LocalTimeToTicksGame(
            Find.TickManager.TicksAbs,
            Find.TickManager.TicksGame,
            dayOfYear,
            hour,
            GenDate.LocalTicksOffsetFromLongitude(longitude),
            GenDate.TicksPerHour,
            GenDate.TicksPerDay,
            GenDate.TicksPerYear);
        Find.TickManager.DebugSetTicksGame((int)newTicksGame);
    }
}
