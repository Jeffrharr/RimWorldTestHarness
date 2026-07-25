using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// SetWeather — the worked example for CONTRIBUTING.md, and a genuinely useful step: weather drives
// RimWorld's sky glow and shadow strength, so a lighting mod that only ever gets screenshotted under
// clear skies is half-tested.
//
// This file plus Mod/Steps/BuiltIn/SetWeatherAction.cs is the ENTIRE step. Nothing else in the repo
// mentions it: no switch arm, no KnownStepTypes entry, no HarnessVerbs entry. That is the point of
// the registry — see CONTRIBUTING.md, "Adding a step".
//
// It does touch one shared file, and that is deliberate rather than a leak: ScenarioResidue.Weather
// had to be added to the residue enum, because the suite planner has to understand a residue kind to
// decide whether it can be undone. A free-form residue string would let a typo silently read as
// "leaves nothing behind", which is the one direction that quietly breaks scenario isolation. Most
// steps reuse an existing flag and add nothing.
public sealed class SetWeatherStep : IStepSpec
{
    // Arg names are consts on the step that owns them. A contributor's step never needs to touch
    // Shared/StepArgs.cs, which holds the built-in vocabulary that predates the registry.
    public const string StepType = "SetWeather";
    public const string WeatherDefArg = "weatherDef";   // WeatherDef defName, e.g. "Rain", "Clear"
    public const string TransitionArg = "instant";      // bool, default true

    public string Type => StepType;
    public ScenarioResidue Residue => ScenarioResidue.Weather;

    // Not live-callable. Changing the weather over a real player's colony is a visible, lasting
    // change to their game, which is the line the companion channel does not cross. The default for
    // anything new is false; this states it for the reader rather than relying on the default.
    public bool LiveCallable => false;

    // The defName can't be checked without a loaded DefDatabase, so that check lives in the action.
    // What CAN be checked offline is checked offline — the arg being present at all, and `instant`
    // parsing as a bool — because a load-time failure costs seconds and an execution-time one costs
    // a whole game boot.
    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!args.TryGetValue(WeatherDefArg, out string? defName) || string.IsNullOrWhiteSpace(defName))
        {
            error = $"'{WeatherDefArg}' is required (a WeatherDef defName, e.g. \"Rain\")";
            return false;
        }

        if (args.TryGetValue(TransitionArg, out string? instant) && !bool.TryParse(instant, out _))
        {
            error = $"'{TransitionArg}' must be true or false (got '{instant}')";
            return false;
        }

        error = null;
        return true;
    }
}
