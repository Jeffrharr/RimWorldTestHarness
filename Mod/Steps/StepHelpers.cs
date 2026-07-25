using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // UnityEngine.ScreenCapture writes asynchronously over an unspecified number of later frames, so
    // "the step returned" is not "the PNG is on disk". Both drivers must wait for the file itself
    // rather than a frame count.
    //
    // The batch driver used to assume a flat 5 frames was enough. It usually was, because a scenario
    // normally has more steps after a screenshot — but the LAST screenshot before something that
    // reads it had only those 5 frames, and an Assert naming it failed with "no screenshot at
    // weather_rain.png" while the file appeared moments later. The live channel already did this
    // properly (a 120-frame budget); sharing one definition here is what stops the two drifting,
    // which is the same reason StepExecutor exists at all.
    public const int ScreenshotWaitBudgetFrames = 120; // ~2s at 60fps

    // Non-empty, not merely present: the file is created before it is filled, so File.Exists alone
    // would hand a reader a zero-byte PNG.
    public static bool ScreenshotComplete(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    // Where the game's log buffer stood when the current scenario started, so an Assert's excerpt can
    // be limited to messages THIS scenario produced.
    //
    // Without it the excerpt is whatever happened to be at the tail of a session-long buffer, which
    // in practice is startup noise — the first live run's packet was nine lines of unrelated
    // "<downloadUrl> missing" warnings from other people's mods. Evidence a judge has to wade through
    // is evidence a judge will skim, and the whole point of attaching the log is to surface the one
    // exception that explains a plausible-looking screenshot.
    private static int _logBaseline;

    public static void MarkLogBaseline() => _logBaseline = Log.Messages.Count();

    public static IEnumerable<LogMessage> MessagesSinceBaseline()
    {
        // RimWorld caps the buffer and drops old entries, so the count can go DOWN. Skipping a stale
        // baseline would silently discard the newest messages — exactly the ones worth having — so
        // fall back to the whole buffer, which is noisy but never misleading.
        List<LogMessage> all = Log.Messages.ToList();
        return all.Count < _logBaseline ? all : all.Skip(_logBaseline);
    }

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
