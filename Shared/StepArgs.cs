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

    // Starts a vanilla GameCondition on the current map so scenarios can exercise condition-driven
    // effects (solar flare, eclipse, ...) that no clock/latitude jump can produce. The condition is
    // registered live; a FastForward step after it lets any fade-in elapse before a probe/screenshot.
    public const string StartConditionType = "StartCondition";
    public const string StartConditionDef = "conditionDef";     // GameConditionDef defName (e.g. "SolarFlare", "Eclipse")
    public const string StartConditionDurationHours = "durationHours"; // float, default 24; <=0 => permanent
    // Back-dates the condition's startTick so it is "born aged": TicksPassed is immediately this many
    // in-game hours, letting a fade-in (e.g. the aurora tint's ~1h ramp) read as already complete
    // without needing real ticks to elapse (FastForward can't advance a scenario-paused clock). float,
    // default 0.
    public const string StartConditionAgedHours = "agedHours";

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

    // Whether PlaceThings/SetTerrain clear their footprint before building: destroy the destroyable
    // things standing in it (mineable rock, chunks, plants, existing buildings) and strip roof, so the
    // area is genuinely under open sky. See SceneClearing for what may and may not be destroyed.
    //
    // Defaults to FALSE, unlike its sibling `unfog`, and the asymmetry is deliberate. Lifting fog only
    // changes what is drawn; clearing permanently deletes map content, and SceneBuilder's cores are
    // also reached from a [DebugAction] that DevActionCatalog exposes over the live companion channel
    // — i.e. one invoke away from a real player's colony, with no undo. A rock-blocked footprint also
    // cannot pass silently (PlaceThings already reports every refused cell), whereas a default-on
    // clear could silently bulldoze. Left unset, scene setup still WARNS when the footprint is roofed,
    // so the omission never buys a green run over a wrongly-lit screenshot.
    public const string SceneClear = "clear"; // bool, default false

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

    // Spawns wild animals into the scene. Reuses the scene family's anchor/offset/unfog keys because
    // it answers "where on the map?" the same way; see AnimalLayout for the row grammar and
    // SceneBuilder.SpawnAnimals for how a kind resolves and a pawn is generated. Wild animals only for
    // now — a non-animal kind is rejected rather than generated without faction/gear.
    public const string SpawnAnimalType = "SpawnAnimal";
    public const string SpawnAnimalKind = "kind";       // PawnKindDef defName; must be an animal (e.g. "Muffalo")
    public const string SpawnAnimalCount = "count";     // int >= 1, default 1
    public const string SpawnAnimalSpacing = "spacing"; // int >= 1, cells between animals in the row, default 2

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
