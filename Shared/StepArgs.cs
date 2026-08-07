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

    // Relative sibling of SetTime. See Steps/BuiltIn/ClockSteps.cs for why both exist.
    public const string AdvanceTimeType = "AdvanceTime";
    public const string AdvanceTimeHours = "hours"; // float, > 0

    public const string FastForwardType = "FastForward";
    public const string FastForwardTicks = "ticks"; // int

    // AdvanceTicks is to FastForward what AdvanceTime is to a simulated hour: it moves TicksGame by
    // an exact amount with no simulation at all. See Steps/BuiltIn/ClockSteps.cs for why a tick-unit
    // jump exists alongside the hour-unit one.
    public const string AdvanceTicksType = "AdvanceTicks";
    public const string AdvanceTicksTicks = "ticks"; // int, > 0

    public const string ProbeType = "Probe";
    public const string ProbeName = "probeName"; // must match a registered IProbe.Name
    public const string ProbeExpectedValue = "expectedValue"; // float
    public const string ProbeTolerance = "tolerance"; // float

    // Which profiling mode `expectedValue` was pinned under: any (default) | profiler | no-profiler.
    // A mismatch against the run's own mode is an ERROR, not a note — profiling rewrites the body of
    // every Harmony-patched method in the load, so a timing number pinned in one mode and checked in
    // the other moves for reasons that have nothing to do with the code under test. Left at `any` for
    // probes that read state rather than time, which is most of them. See Shared/RunProfiling.cs.
    public const string ProbePinnedUnder = "pinnedUnder";

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
    public const string SceneDef = "def";       // ThingDef (PlaceThings), TerrainDef (SetTerrain) or RoofDef (SetRoof) defName
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

    // Paints a rectangle of roof. This is the one piece of map state a scenario could not build
    // before: RimWorld only ever roofs a cell as a consequence of *play* (enclosing a room hands the
    // job to AutoBuildRoofAreaSetter over the following ticks, and a roof-area designation is a
    // colonist work order), so a step that spawns walls into a fixture gets a walled shell with no
    // roof on it and no way to ask for one. Everything that reads the roof grid — indoor lighting,
    // shelter, temperature, and eaves, where roof exists over cells no wall encloses — was therefore
    // unreachable from a scenario.
    //
    // Width/height/anchor/offset are SetTerrain's rectangle grammar, deliberately: a scenario that
    // paints a floor and then roofs it writes the same rect twice.
    public const string SetRoofType = "SetRoof";
    public const string SetRoofWidth = "width";   // int >= 1
    public const string SetRoofHeight = "height"; // int >= 1

    // `def` value that REMOVES roof over the rect instead of painting one. Spelled as a def value
    // rather than a separate step because roofing and unroofing a rectangle are the same operation
    // over the same grammar, and a scenario carving a courtyard out of a slab it just painted would
    // otherwise need two vocabularies for one idea.
    public const string SetRoofNoneDef = "None";

    public const string LookAtType = "LookAt";
    public const string LookAtZoom = "zoom"; // float > 0, CameraDriver root size; omit to keep current

    // Spawns pawns into the scene — wild animals, player colonists, or hostile-faction raiders,
    // decided by `faction`. Reuses the scene family's anchor/offset/unfog/clear keys because it
    // answers "where on the map?" the same way; see PawnLayout for the row grammar and
    // SceneBuilder.SpawnPawns for how a kind/faction resolves and a pawn is generated. `clear`
    // (default false) destroys destroyables and strips roof in each spawn cell first, so a pawn can
    // land where a wall or rock stood.
    public const string SpawnPawnType = "SpawnPawn";
    public const string SpawnPawnKind = "kind";         // PawnKindDef defName (e.g. "Muffalo", "Colonist", "Pirate")
    public const string SpawnPawnCount = "count";       // int >= 1, default 1
    public const string SpawnPawnSpacing = "spacing";   // int >= 1, cells between pawns in the row, default 2

    // Which faction the spawned pawns belong to, which is what makes a humanlike a colonist or a
    // raider rather than a neutral drifter: "wild" (default, no faction — animals, wild men),
    // "player" (your colony), or "hostile" (a deterministic enemy faction of the player).
    public const string SpawnPawnFaction = "faction";   // wild (default) | player | hostile

    // Force the pawn's gender. Omit to let generation pick. "male" | "female".
    public const string SpawnPawnGender = "gender";

    // Health conditions to apply after generation, as a semicolon-separated list. Each entry is a
    // HediffDef defName, optionally targeting a body part with "@BodyPartDef" and/or setting a
    // severity with ":<float>":
    //   "Flu:0.4; ToxicBuildup:0.7"          — whole-body conditions at a chosen severity
    //   "MissingBodyPart@Leg; BionicArm@Arm" — targeted at the first matching body part
    // Body parts are resolved against the kind's own body, so a typo or a part the race lacks fails
    // the step before any pawn is spawned rather than logging mid-run.
    public const string SpawnPawnHediffs = "hediffs";

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

    // Profiling under Dubs Performance Analyzer. Profile is the third COMPOSITE: it desugars at load
    // time into ProfileStart / ProfileMeasure / ProfileStop, which are also usable individually when
    // the window should be "whatever these other steps do" rather than a fixed number of frames.
    // See ProfileExpander for the desugaring and Mod/Profiling/DubsAnalyzer.cs for the adapter.
    public const string ProfileType = "Profile";
    public const string ProfileStartType = "ProfileStart";
    public const string ProfileMeasureType = "ProfileMeasure";
    public const string ProfileStopType = "ProfileStop";

    // Names the harvested table in the report, and the handle a ProfileAssert step refers to. Required:
    // an unnamed table cannot be asserted on, and a scenario that profiled and never asserted is the
    // green-run-means-less failure in miniature.
    public const string ProfileName = "name";

    // Label prefix rows are filtered to, matched against the analyzer's key for the patch method —
    // "Namespace.Type:Method(params)", e.g. "CelestialLighting.Patch_Foo:Postfix(ref Vector2&)" —
    // in practice a mod's root namespace. Required, and required to be non-empty: unfiltered means
    // every profiled method in the load, which is thousands of rows of vanilla in the report.
    public const string ProfilePrefix = "prefix";

    // Rendered frames to measure. The analyzer records one sample per Root_Play.Update, so this is
    // frames, not ticks and not seconds.
    public const string ProfileFrames = "frames";

    // Frames to burn between activating the profiler and zeroing its counters. Not politeness: the
    // analyzer TRANSPLANTS timing calls into every profiled method, so the first invocation of each
    // one pays JIT for the rewritten body. Measuring from frame zero attributes that one-off cost to
    // whichever patch happened to run first.
    public const string ProfileWarmupFrames = "warmupFrames";

    // Which analyzer entry to profile. Only "harmony" (every non-analyzer Harmony patch in the load)
    // is implemented; spelled as an arg so adding "tick" or "gui" later needs no new step type.
    public const string ProfileEntry = "entry";

    // Game speed to hold during the window. Defaults to "normal" rather than leaving whatever the
    // scenario had, because a scenario that jumped the clock leaves the game PAUSED, and profiling a
    // paused colony measures a load of tick-driven patches that never fire — a table of zeroes that
    // reads as "this mod is free".
    public const string ProfileTimeSpeed = "timeSpeed"; // paused | normal | fast | superfast | ultrafast

    // Asserts one number out of a harvested table, folding into the same ProbeChecks gate an ordinary
    // Probe step uses.
    public const string ProfileAssertType = "ProfileAssert";
    public const string ProfileAssertTable = "table";   // a ProfileStop/Profile step's `name`
    public const string ProfileAssertLabel = "label";   // exact or unique-substring row label; "*" = table totals
    public const string ProfileAssertMetric = "metric"; // see ProfileMetrics.Known
    // Exactly one bound form must be given: expectedValue+tolerance (two-sided, the Probe step's
    // shape), or max, or min. Performance assertions are usually one-sided — "this must not exceed" —
    // and forcing them through expected±tolerance is how a useful gate ends up unwritten.
    public const string ProfileAssertExpectedValue = "expectedValue";
    public const string ProfileAssertTolerance = "tolerance";
    public const string ProfileAssertMax = "max";
    public const string ProfileAssertMin = "min";

    // TickLapse is Timelapse's short-interval sibling: same numbered-PNG-sequence output and the
    // same stitching downstream, but each frame steps the clock by TICKS rather than hours, so it
    // films an effect that animates on the tick counter instead of on the hour of day. See
    // TickLapseExpander for why a sub-hour Timelapse is not a substitute.
    public const string TickLapseType = "TickLapse";
    public const string TickLapseTicks = "ticks";                   // int > 0, default 10
    public const string TickLapseSteps = "steps";                   // int > 0, default 120
    public const string TickLapseFileNamePrefix = "fileNamePrefix"; // default "ticklapse"
    public const string TickLapseSettleFrames = "settleFrames";     // int >= 0, default 2
    public const string TickLapseFps = "fps";                       // int 1..60, default 20
}
