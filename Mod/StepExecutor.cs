using System;
using System.Collections.Generic;
using RimWorld;
using RimWorldTestHarness.Mod.Features;
using RimWorldTestHarness.Mod.Probes;
using RimWorldTestHarness.Shared;
using UnityEngine;
using Verse;

namespace RimWorldTestHarness.Mod;

// The result of running a single dev-action against the live game. Deliberately a plain data bag
// with no game types, so BOTH drivers can translate it into their own output shape: the batch
// ScenarioDriver folds it into a ScenarioReport, the live LiveCommandDriver folds it into a
// LiveResponse. Some actions can't complete in the frame they start — a screenshot needs a few
// frames to flush to disk, a FastForward needs real ticks to elapse — so the outcome carries the
// wait the driver must honour before it treats the command as finished.
public sealed class StepOutcome
{
    // Immediate effects the driver should apply / record.
    public float? ForcedLatitude;   // SetTile: driver copies into HarnessRuntime.ForcedLatitude
    public float? ProbeValue;       // Probe: the raw reading (caller decides how to interpret it)
    public string? ScreenshotPath;  // Screenshot: absolute path the PNG is being written to
    public string? Error;           // non-null => the action failed; nothing else is meaningful

    // Post-effect waits the driver must satisfy before the NEXT command runs.
    public int WaitFrames;                  // screenshot flush countdown (frames)
    public bool WaitFastForward;            // true => wait until TicksGame >= FastForwardTargetTicksGame
    public int FastForwardTargetTicksGame;

    public static StepOutcome Fail(string error) => new StepOutcome { Error = error };
}

// The bits of live-game context a step needs that differ between callers. Kept tiny and explicit
// (rather than reaching for Find.* inside StepExecutor) so screenshot output policy stays with the
// caller: batch writes into the run's report folder, the live channel writes into its session dir.
public sealed class StepContext
{
    public Map Map { get; }

    // Maps a screenshot's caller-facing name/arg to the absolute file path to capture to.
    public Func<string, string> ResolveScreenshotPath { get; }

    public StepContext(Map map, Func<string, string> resolveScreenshotPath)
    {
        Map = map;
        ResolveScreenshotPath = resolveScreenshotPath;
    }
}

// The single, shared source of truth for what each harness dev-action DOES to the live game.
// Extracted verbatim from ScenarioDriver's old Run* handlers so batch and live behave identically
// and can never drift. Stateless: it takes primitives + a StepContext in and returns a StepOutcome
// out, holding no per-run state of its own (the drivers own that).
public static class StepExecutor
{
    // Runs one action. Any exception (e.g. a malformed numeric arg) is turned into a failed
    // StepOutcome rather than propagating out through the Root_Play.Update postfix that ultimately
    // calls this — a bad scenario/command should surface as an error entry, not crash the frame.
    public static StepOutcome Execute(string actionType, IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        try
        {
            return Dispatch(actionType, args, ctx);
        }
        catch (Exception ex)
        {
            return StepOutcome.Fail($"Action '{actionType}' threw: {ex.Message}");
        }
    }

    private static StepOutcome Dispatch(string actionType, IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        switch (actionType)
        {
            case StepArgs.SetTileType:
                return SetTile(args);
            case StepArgs.SetSeasonType:
                return SetSeason(args, ctx);
            case StepArgs.SetTimeType:
                return SetTime(args, ctx);
            case StepArgs.FastForwardType:
                return FastForward(args);
            case StepArgs.ProbeType:
                return Probe(args, ctx);
            case StepArgs.ScreenshotType:
                return Screenshot(args, ctx);
            case StepArgs.SetFeatureType:
                return SetFeature(args);
            case StepArgs.WaitType:
                return Wait(args);
            case StepArgs.TimelapseType:
                // TimelapseExpander desugars these at load time, so one reaching the executor means
                // its args failed validation and it was deliberately left un-expanded. The specific
                // reason is already in the report via ScenarioSpec.LoadErrors; this makes the step
                // itself fail too, so the run can't look complete while a whole sweep is missing.
                return StepOutcome.Fail(
                    "Timelapse step was not expanded — its args are invalid (see the earlier load error)");
            default:
                return StepOutcome.Fail($"Unknown action type '{actionType}'");
        }
    }

    // A SetTile just overrides latitude in place (Patch_ForcedLatitude) rather than actually
    // regenerating the landing tile (which would drag in WorldGenerator — see Patch_ForcedLatitude.cs
    // and Fixtures/README.md). The driver persists the returned value into HarnessRuntime.
    private static StepOutcome SetTile(IReadOnlyDictionary<string, string> args)
    {
        return new StepOutcome { ForcedLatitude = float.Parse(args[StepArgs.SetTileLatitude]) };
    }

    private static StepOutcome SetSeason(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        int dayOfYear = int.Parse(args[StepArgs.SetSeasonDayOfYear]);
        float longitude = LongitudeOf(ctx.Map);
        float currentHour = GenDate.HourFloat(Find.TickManager.TicksAbs, longitude);
        JumpToLocalTime(dayOfYear, currentHour, longitude);
        return new StepOutcome();
    }

    private static StepOutcome SetTime(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        float hour = float.Parse(args[StepArgs.SetTimeHour]);
        float longitude = LongitudeOf(ctx.Map);
        int currentDayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longitude);
        JumpToLocalTime(currentDayOfYear, hour, longitude);
        return new StepOutcome();
    }

    private static StepOutcome FastForward(IReadOnlyDictionary<string, string> args)
    {
        int ticks = int.Parse(args[StepArgs.FastForwardTicks]);
        // Unlike SetSeason/SetTime (which jump the clock directly), FastForward needs real ticks to
        // actually pass, so make sure the game isn't sitting paused while we wait.
        Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        return new StepOutcome
        {
            WaitFastForward = true,
            FastForwardTargetTicksGame = Find.TickManager.TicksGame + ticks,
        };
    }

    private static StepOutcome Probe(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string probeName = args[StepArgs.ProbeName];
        if (!ProbeRegistry.TryGet(probeName, out IProbe? probe) || probe == null)
            return StepOutcome.Fail($"No probe registered named '{probeName}'");

        return new StepOutcome { ProbeValue = probe.Read(ctx.Map) };
    }

    private static StepOutcome Screenshot(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string fileName = args[StepArgs.ScreenshotFileName];
        string path = ctx.ResolveScreenshotPath(fileName);
        // Hidden by default: a batch run forces DevMode, so every frame would otherwise carry the
        // dev toolbar on top of the colonist bar, alerts, letters and bottom bar — noise in a
        // single screenshot, and 24x the noise in a timelapse. Opt back in with hideUi=false when
        // the UI itself is what's under test.
        bool hideUi = ParseBool(args, StepArgs.ScreenshotHideUi, defaultValue: true);
        // Shared capture core — the same one the [DebugAction] dev-menu entry uses.
        HarnessDebugActions.CaptureScreenshotTo(path, hideUi);
        // CaptureScreenshot writes asynchronously over the next few frames — the driver waits
        // WaitFrames before the next command (or, for live mode, before returning the PNG).
        return new StepOutcome { ScreenshotPath = path, WaitFrames = 5 };
    }

    // Flips a named runtime feature flag in the mod under test via FeatureRegistry (which the mod's
    // dev-only bridge assembly populated at startup). Takes no game state — the effect is entirely
    // in the target mod's static flag — so a scenario can bracket a Screenshot pair with off/on
    // SetFeature steps for an A/B visual diff. An unregistered name becomes a scenario error rather
    // than a crash, matching how Probe handles an unknown probe name.
    private static StepOutcome SetFeature(IReadOnlyDictionary<string, string> args)
    {
        string name = args[StepArgs.SetFeatureName];
        bool enabled = bool.Parse(args[StepArgs.SetFeatureEnabled]);
        if (!FeatureRegistry.TrySet(name, enabled))
            return StepOutcome.Fail($"No feature registered named '{name}'");

        return new StepOutcome();
    }

    // Idles for a number of rendered frames. Exists so a scenario can let the game settle before
    // the next step observes it: a clock jump doesn't necessarily update the glow grid and shadow
    // direction in the same frame, and a screenshot taken too eagerly would capture stale lighting.
    // Unlike FastForward this burns frames, not game ticks — the clock doesn't advance.
    private static StepOutcome Wait(IReadOnlyDictionary<string, string> args)
    {
        int frames = int.Parse(args[StepArgs.WaitFrames]);
        if (frames < 0)
            return StepOutcome.Fail($"'{StepArgs.WaitFrames}' must not be negative (got {frames})");

        return new StepOutcome { WaitFrames = frames };
    }

    // Absent means "use the caller's default" rather than "false", so adding an optional flag to a
    // step never changes what existing scenario files do.
    private static bool ParseBool(IReadOnlyDictionary<string, string> args, string key, bool defaultValue) =>
        args.TryGetValue(key, out string? raw) ? bool.Parse(raw) : defaultValue;

    private static float LongitudeOf(Map map) => Find.WorldGrid.LongLatOf(map.Tile).x;

    // Jumps the game clock so GenDate.DayOfYear/HourFloat read back exactly dayOfYear/hour at the
    // given longitude, staying within the current in-game year. Mirrors GenDate's own
    // DayOfYear/HourOfDay derivation (PositiveModRemap over TicksAbs + a longitude offset) in
    // reverse.
    private static void JumpToLocalTime(int dayOfYear, float hour, float longitude)
    {
        long offset = GenDate.LocalTicksOffsetFromLongitude(longitude);
        long currentAbs = Find.TickManager.TicksAbs;
        long currentLocal = currentAbs + offset;
        long yearStart = currentLocal - PositiveMod(currentLocal, GenDate.TicksPerYear);

        int dayTick = Mathf.Clamp(Mathf.RoundToInt(hour * GenDate.TicksPerHour), 0, GenDate.TicksPerDay - 1);
        long targetLocal = yearStart + (long)dayOfYear * GenDate.TicksPerDay + dayTick;
        long targetAbs = targetLocal - offset;

        long gameStartAbsTick = currentAbs - Find.TickManager.TicksGame;
        Find.TickManager.DebugSetTicksGame((int)(targetAbs - gameStartAbsTick));
    }

    private static long PositiveMod(long value, long modulus) => ((value % modulus) + modulus) % modulus;
}
