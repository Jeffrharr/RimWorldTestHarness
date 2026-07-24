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
    public const string ScreenshotHideUi = "hideUi";     // bool, default "true" — see StepExecutor

    public const string SetFeatureType = "SetFeature";
    public const string SetFeatureName = "featureName"; // must match a setter registered in FeatureRegistry
    public const string SetFeatureEnabled = "enabled";  // bool: "true"/"false"

    public const string WaitType = "Wait";
    public const string WaitFrames = "frames"; // int >= 0, rendered frames to idle before the next step

    // Timelapse is the one composite step: it never reaches the executor, because
    // TimelapseExpander desugars it at load time into a SetTime/Wait/Screenshot triple per frame.
    // See TimelapseExpander for the range semantics and defaults.
    public const string TimelapseType = "Timelapse";
    public const string TimelapseFromHour = "fromHour";           // float, default 0
    public const string TimelapseToHour = "toHour";               // float, default 24 (exclusive)
    public const string TimelapseStepHours = "stepHours";         // float > 0, default 1
    public const string TimelapseFileNamePrefix = "fileNamePrefix"; // default "timelapse"
    public const string TimelapseSettleFrames = "settleFrames";   // int >= 0, default 2
    public const string TimelapseFps = "fps";                     // int 1..60, default 12
}
