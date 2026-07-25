using System;
using System.Collections.Generic;
using RimWorld;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// Starts a live GameCondition on the current map (solar flare, eclipse, ...) so scenarios can
// exercise condition-driven visual effects that no clock or latitude jump can produce. Resolved by
// defName via GetNamedSilentFail so an unknown/removed def fails the step cleanly instead of
// throwing. durationHours defaults to a full day; <= 0 makes it permanent (MakeCondition with a
// negative duration is the vanilla "no expiry" sentinel). The condition takes effect immediately; a
// FastForward step after this lets any fade-in (e.g. the aurora tint's ramp) elapse before a probe
// or screenshot reads it.
public sealed class StartConditionAction : IStepAction
{
    public string Type => StepArgs.StartConditionType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string defName = args[StepArgs.StartConditionDef];
        GameConditionDef def = DefDatabase<GameConditionDef>.GetNamedSilentFail(defName);
        if (def == null)
            return StepOutcome.Fail($"No GameConditionDef named '{defName}'");

        float hours = args.TryGetValue(StepArgs.StartConditionDurationHours, out string? h)
            ? float.Parse(h)
            : 24f;
        int durationTicks = hours <= 0f ? -1 : (int)Math.Round(hours * GenDate.TicksPerHour);

        GameCondition cond = GameConditionMaker.MakeCondition(def, durationTicks);
        ctx.Map.gameConditionManager.RegisterCondition(cond);

        // Optionally back-date startTick so the condition is "born aged": TicksPassed becomes
        // agedHours immediately (GameCondition.TicksPassed == TicksGame - startTick).
        // RegisterCondition set startTick to now, so subtracting here makes a fade-in that keys on
        // TicksPassed (e.g. the aurora tint ramp) read as already elapsed — without FastForward,
        // which can't advance the scenario-paused clock. Kept small relative to duration so the
        // condition doesn't read as already expired.
        if (args.TryGetValue(StepArgs.StartConditionAgedHours, out string? aged))
        {
            int agedTicks = (int)Math.Round(float.Parse(aged) * GenDate.TicksPerHour);
            cond.startTick -= agedTicks;
        }

        return new StepOutcome();
    }
}
