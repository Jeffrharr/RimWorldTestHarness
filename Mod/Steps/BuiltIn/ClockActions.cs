using System.Collections.Generic;
using RimWorld;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of the clock/world steps. Behaviour is unchanged from when these were
// private methods on StepExecutor — only their home moved, so each step's two halves sit next to
// each other and neither is a switch arm anyone has to find.

// A SetTile just overrides latitude in place (Patch_ForcedLatitude) rather than actually
// regenerating the landing tile (which would drag in WorldGenerator — see Patch_ForcedLatitude.cs
// and Fixtures/README.md). The driver persists the returned value into HarnessRuntime.
public sealed class SetTileAction : IStepAction
{
    public string Type => StepArgs.SetTileType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx) =>
        new StepOutcome { ForcedLatitude = float.Parse(args[StepArgs.SetTileLatitude]) };
}

public sealed class SetSeasonAction : IStepAction
{
    public string Type => StepArgs.SetSeasonType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        int dayOfYear = int.Parse(args[StepArgs.SetSeasonDayOfYear]);
        float longitude = StepHelpers.LongitudeOf(ctx.Map);
        float currentHour = GenDate.HourFloat(Find.TickManager.TicksAbs, longitude);
        StepHelpers.JumpToLocalTime(dayOfYear, currentHour, longitude);
        return new StepOutcome();
    }
}

public sealed class SetTimeAction : IStepAction
{
    public string Type => StepArgs.SetTimeType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        float hour = float.Parse(args[StepArgs.SetTimeHour]);
        float longitude = StepHelpers.LongitudeOf(ctx.Map);
        int currentDayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longitude);
        StepHelpers.JumpToLocalTime(currentDayOfYear, hour, longitude);
        return new StepOutcome();
    }
}

// The game-touching half of AdvanceTime. See Shared/Steps/BuiltIn/ClockSteps.cs for why this exists
// alongside SetTime.
//
// DebugSetTicksGame, the same lever SetTime pulls, so this is a clock JUMP and not simulation — no
// ticks are run and the world does not live through the skipped hours. The difference is only that
// the destination is computed from where the clock already is, which is what makes a sequence of
// these monotonic.
public sealed class AdvanceTimeAction : IStepAction
{
    public string Type => StepArgs.AdvanceTimeType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        float hours = float.Parse(args[StepArgs.AdvanceTimeHours]);

        // Rounded, not truncated: a 0.25-hour step is 625 ticks exactly, but 0.1 is 250.0000...
        // in float and truncation would shave a tick off every frame, drifting a long sweep.
        int ticks = (int)System.Math.Round(hours * GenDate.TicksPerHour);
        Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ticks);

        return new StepOutcome();
    }
}

public sealed class FastForwardAction : IStepAction
{
    public string Type => StepArgs.FastForwardType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
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
}

// Idles for a number of rendered frames. Exists so a scenario can let the game settle before the
// next step observes it: a clock jump doesn't necessarily update the glow grid and shadow direction
// in the same frame, and a screenshot taken too eagerly would capture stale lighting. Unlike
// FastForward this burns frames, not game ticks — the clock doesn't advance.
public sealed class WaitAction : IStepAction
{
    public string Type => StepArgs.WaitType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        int frames = int.Parse(args[StepArgs.WaitFrames]);
        if (frames < 0)
            return StepOutcome.Fail($"'{StepArgs.WaitFrames}' must not be negative (got {frames})");

        return new StepOutcome { WaitFrames = frames };
    }
}
