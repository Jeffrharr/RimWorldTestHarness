namespace RimWorldTestHarness.Shared;

// Documents the Args keys each ScenarioStep.Type expects. Not enforced by the type system
// (ScenarioStep.Args is a plain dictionary — see ScenarioSpec.cs for why); the Mod-side step
// executor is the actual source of truth once it exists. Centralizing the key NAMES here at
// least keeps Scenarios/*.json and the (not-yet-written) executor from drifting on spelling.
public static class StepArgs
{
    public const string SetTileType = "SetTile";
    public const string SetTileLatitude = "latitude"; // float degrees, -90..90

    public const string SetSeasonType = "SetSeason";
    public const string SetSeasonDayOfYear = "dayOfYear"; // int, 0..59 (GenDate.DaysPerYear)

    public const string SetTimeType = "SetTime";
    public const string SetTimeHour = "hour"; // float, 0..24

    public const string FastForwardType = "FastForward";
    public const string FastForwardTicks = "ticks"; // int

    public const string ProbeType = "Probe";
    public const string ProbeName = "probeName"; // must match a registered IProbe.Name
    public const string ProbeExpectedValue = "expectedValue"; // float
    public const string ProbeTolerance = "tolerance"; // float

    public const string ScreenshotType = "Screenshot";
    public const string ScreenshotFileName = "fileName"; // written under the run's report folder
}
