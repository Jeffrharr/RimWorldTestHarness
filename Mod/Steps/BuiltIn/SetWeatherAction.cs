using System.Collections.Generic;
using RimWorld;
using RimWorldTestHarness.Shared.Steps.BuiltIn;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of SetWeather. See Shared/Steps/BuiltIn/SetWeatherStep.cs for the pure half
// and the rationale; together they are the whole step.
public sealed class SetWeatherAction : IStepAction
{
    public string Type => SetWeatherStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string defName = args[SetWeatherStep.WeatherDefArg];
        // GetNamedSilentFail, not GetNamed: an unknown or removed defName should fail this step with
        // a readable reason, not throw inside the tick loop. Same treatment Probe gives an unknown
        // probe name and StartCondition gives an unknown GameConditionDef.
        WeatherDef def = DefDatabase<WeatherDef>.GetNamedSilentFail(defName);
        if (def == null)
            return StepOutcome.Fail($"No WeatherDef named '{defName}'");

        bool instant = StepHelpers.ParseBool(args, SetWeatherStep.TransitionArg, defaultValue: true);
        ctx.Map.weatherManager.TransitionTo(def);

        // TransitionTo starts a blend rather than switching outright: it sets lastWeather = curWeather
        // and curWeatherAge = 0, and everything the sky reads (RainRate, CurWindSpeedFactor, the sky
        // target lerp) is a Mathf.Lerp between the two on TransitionLerpFactor == curWeatherAge /
        // TransitionTicks. A screenshot taken immediately would therefore show a half-mixed sky.
        //
        // Ageing the transition to exactly TransitionTicks puts that factor at 1.0 — fully the new
        // weather — which is what a scenario means by "set the weather". Vanilla's own constant
        // rather than a large sentinel: curWeatherAge keeps incrementing each tick, so a huge value
        // would be arithmetic waiting to overflow, and 4000 is the exact point the blend completes.
        // instant=false leaves the natural transition for scenarios that want to photograph it.
        if (instant)
            ctx.Map.weatherManager.curWeatherAge = (int)WeatherManager.TransitionTicks;

        // One frame for the sky glow to take the new weather into account, matching what the scene
        // steps do after changing geometry.
        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}
