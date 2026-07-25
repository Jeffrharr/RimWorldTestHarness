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

    // Scene setup. Shared by PlaceThings/SetTerrain/LookAt because all three answer "where on the
    // map?" the same way, and a scenario author shouldn't have to remember three spellings of it.
    // Anchors and offsets are whole cells; see SceneLayout for the grammar and SceneBuilder for how
    // an anchor resolves against a live map.
    public const string SceneDef = "def";       // ThingDef (PlaceThings) or TerrainDef (SetTerrain) defName
    public const string SceneAnchor = "anchor"; // "center" (default) or absolute map cells "125,125"
    public const string SceneOffset = "offset"; // "dx,dz" whole cells from the anchor, default "0,0"

    // Whether PlaceThings/SetTerrain lift fog of war after building. Defaults to TRUE because a
    // freshly generated colony has only a small revealed pocket, and RimWorld draws neither terrain
    // nor things in fogged cells — so a scene built at map centre is invisible, with every step still
    // reporting success. Opt out with "false" when a scenario genuinely wants fog left alone.
    public const string SceneUnfog = "unfog"; // bool, default true

    public const string PlaceThingsType = "PlaceThings";
    public const string PlaceThingsLayout = "layout";   // "grid" (default) | "row" | "cells"
    public const string PlaceThingsStuff = "stuff";     // stuff ThingDef; omit for the def's default
    public const string PlaceThingsRot = "rot";         // North (default) | East | South | West
    public const string PlaceThingsCols = "cols";       // grid only, int >= 1
    public const string PlaceThingsRows = "rows";       // grid only, int >= 1
    public const string PlaceThingsCount = "count";     // row only, int >= 1
    public const string PlaceThingsAxis = "axis";       // row only, "x" (default) | "z"
    public const string PlaceThingsSpacing = "spacing"; // grid/row, int >= 1, cells between placements
    public const string PlaceThingsCells = "cells";     // cells only, "0,0; 4,0; 8,-3"

    public const string SetTerrainType = "SetTerrain";
    public const string SetTerrainWidth = "width";   // int >= 1
    public const string SetTerrainHeight = "height"; // int >= 1

    public const string LookAtType = "LookAt";
    public const string LookAtZoom = "zoom"; // float > 0, CameraDriver root size; omit to keep current

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
