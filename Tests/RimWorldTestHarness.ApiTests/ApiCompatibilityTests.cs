using Mono.Cecil;

namespace RimWorldTestHarness.ApiTests;

// Verifies the vanilla RimWorld/Unity API surface the harness depends on still exists — both the
// members our Harmony patches directly target (Patch_ForceDevMode, Patch_ForcedLatitude,
// Patch_DriveScenario) AND the members we never call ourselves but whose *behavior* our whole
// design leans on: Root_Entry.Start()'s autostart-save chain (SaveGameFilesUtility.
// GetAutostartSaveFile -> GameDataSaveLoader.LoadGame) is what actually loads Fixtures/*.rws — if
// Ludeon renames or removes any link in that chain, a scenario run would hang forever waiting for
// a map that never loads, with no obvious error. Run after every RimWorld update. Failures mean
// the harness needs updating before trusting any scenario result.
[TestFixture]
[Category("RequiresGameDll")]
public class ApiCompatibilityTests
{
    private const string FallbackAssemblyCSharpPath =
        "/home/deck/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux_Data/Managed/Assembly-CSharp.dll";
    private const string FallbackScreenCaptureModulePath =
        "/home/deck/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux_Data/Managed/UnityEngine.ScreenCaptureModule.dll";

    private static string AssemblyCSharpPath =>
        Environment.GetEnvironmentVariable("RIMWORLD_ASSEMBLY") ?? FallbackAssemblyCSharpPath;
    private static string ScreenCaptureModulePath =>
        Environment.GetEnvironmentVariable("RIMWORLD_SCREENCAPTURE_ASSEMBLY") ?? FallbackScreenCaptureModulePath;

    private ModuleDefinition _game = null!;
    private ModuleDefinition _screenCapture = null!;

    [OneTimeSetUp]
    public void LoadAssemblies()
    {
        if (!File.Exists(AssemblyCSharpPath))
            Assert.Ignore($"Assembly-CSharp.dll not found at {AssemblyCSharpPath} — set RIMWORLD_ASSEMBLY to run these tests.");
        if (!File.Exists(ScreenCaptureModulePath))
            Assert.Ignore($"UnityEngine.ScreenCaptureModule.dll not found at {ScreenCaptureModulePath} — set RIMWORLD_SCREENCAPTURE_ASSEMBLY to run these tests.");
        _game = ModuleDefinition.ReadModule(AssemblyCSharpPath);
        _screenCapture = ModuleDefinition.ReadModule(ScreenCaptureModulePath);
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _game?.Dispose();
        _screenCapture?.Dispose();
    }

    // --- Prefs.DevMode (Patch_ForceDevMode's Harmony target) ---

    [Test]
    public void Prefs_DevMode_GetterExists()
    {
        var type = GetType(_game, "Verse.Prefs");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "DevMode")?.GetMethod;
        Assert.That(getter, Is.Not.Null, "Prefs.DevMode getter no longer exists — Patch_ForceDevMode's Harmony target is gone");
    }

    // --- WorldGrid.LongLatOf(PlanetTile) (Patch_ForcedLatitude's Harmony target) ---

    [Test]
    public void WorldGrid_LongLatOf_PlanetTileOverloadExists()
    {
        var type = GetType(_game, "RimWorld.Planet.WorldGrid");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "LongLatOf" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "RimWorld.Planet.PlanetTile");
        Assert.That(method, Is.Not.Null,
            "WorldGrid.LongLatOf(PlanetTile) no longer exists (or its parameter type changed) — Patch_ForcedLatitude's Harmony target is gone");
    }

    [Test]
    public void WorldGrid_LongLatOf_ReturnsVector2()
    {
        var type = GetType(_game, "RimWorld.Planet.WorldGrid");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "LongLatOf" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "RimWorld.Planet.PlanetTile");
        Assert.That(method?.ReturnType.FullName, Is.EqualTo("UnityEngine.Vector2"),
            "WorldGrid.LongLatOf(PlanetTile) no longer returns Vector2 — Patch_ForcedLatitude postfix's ref Vector2 __result would stop binding");
    }

    // --- Root_Play.Update (Patch_DriveScenario's Harmony target) ---

    [Test]
    public void RootPlay_Update_Exists()
    {
        var type = GetType(_game, "Verse.Root_Play");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "Update" && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null, "Root_Play.Update() no longer exists — Patch_DriveScenario's Harmony target is gone, ScenarioDriver.Tick() would never run");
    }

    // --- Vanilla autostart-save chain (Root_Entry.Start's own call graph — we never call these
    // ourselves, but ScenarioDriver's design assumes vanilla still wires them together this way) ---

    [Test]
    public void RootEntry_Start_Exists()
    {
        var type = GetType(_game, "Verse.Root_Entry");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "Start" && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null, "Root_Entry.Start() no longer exists — the vanilla autostart-save entry point is gone");
    }

    [Test]
    public void SaveGameFilesUtility_GetAutostartSaveFile_Exists()
    {
        var type = GetType(_game, "Verse.SaveGameFilesUtility");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "GetAutostartSaveFile" && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null,
            "SaveGameFilesUtility.GetAutostartSaveFile() no longer exists — autostart.rws would never be found regardless of Prefs.DevMode");
    }

    [Test]
    public void GameDataSaveLoader_LoadGame_FileInfoOverloadExists()
    {
        var type = GetType(_game, "Verse.GameDataSaveLoader");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "LoadGame" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.IO.FileInfo");
        Assert.That(method, Is.Not.Null,
            "GameDataSaveLoader.LoadGame(FileInfo) no longer exists — Root_Entry.Start() would fail to load the autostart save even if found");
    }

    // --- LongEventHandler.ShouldWaitForEvent (ScenarioDriver.Tick's guard, mirroring
    // Root_Play.Update's own early-return so the postfix doesn't advance a step mid-long-event) ---

    [Test]
    public void LongEventHandler_ShouldWaitForEvent_GetterExists()
    {
        var type = GetType(_game, "Verse.LongEventHandler");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "ShouldWaitForEvent")?.GetMethod;
        Assert.That(getter, Is.Not.Null, "LongEventHandler.ShouldWaitForEvent getter no longer exists");
    }

    // --- GenDate (ScenarioDriver.JumpToLocalTime's tick-math) ---

    [Test]
    public void GenDate_TickConstants_StillMatchHardcodedValues()
    {
        var type = GetType(_game, "RimWorld.GenDate");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            AssertConstEquals(type!, "TicksPerHour", 2500);
            AssertConstEquals(type!, "TicksPerDay", 60000);
            AssertConstEquals(type!, "TicksPerYear", 3600000);
            AssertConstEquals(type!, "DaysPerYear", 60);
        });
    }

    [Test]
    public void GenDate_DayOfYear_SignatureExists()
    {
        var type = GetType(_game, "RimWorld.GenDate");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "DayOfYear" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "System.Int64" &&
            m.Parameters[1].ParameterType.FullName == "System.Single");
        Assert.That(method, Is.Not.Null, "GenDate.DayOfYear(long, float) no longer exists");
    }

    [Test]
    public void GenDate_HourFloat_SignatureExists()
    {
        var type = GetType(_game, "RimWorld.GenDate");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "HourFloat" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "System.Int64" &&
            m.Parameters[1].ParameterType.FullName == "System.Single");
        Assert.That(method, Is.Not.Null, "GenDate.HourFloat(long, float) no longer exists");
    }

    [Test]
    public void GenDate_LocalTicksOffsetFromLongitude_SignatureExists()
    {
        var type = GetType(_game, "RimWorld.GenDate");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "LocalTicksOffsetFromLongitude" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.Single");
        Assert.That(method, Is.Not.Null, "GenDate.LocalTicksOffsetFromLongitude(float) no longer exists");
    }

    // --- TickManager (ScenarioDriver's clock control) ---

    [Test]
    public void TickManager_DebugSetTicksGame_SignatureExists()
    {
        var type = GetType(_game, "Verse.TickManager");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "DebugSetTicksGame" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.Int32");
        Assert.That(method, Is.Not.Null, "TickManager.DebugSetTicksGame(int) no longer exists");
    }

    [Test]
    public void TickManager_TicksGame_GetterExists()
    {
        var type = GetType(_game, "Verse.TickManager");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "TicksGame")?.GetMethod;
        Assert.That(getter, Is.Not.Null, "TickManager.TicksGame getter no longer exists");
    }

    [Test]
    public void TickManager_TicksAbs_GetterExists()
    {
        var type = GetType(_game, "Verse.TickManager");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "TicksAbs")?.GetMethod;
        Assert.That(getter, Is.Not.Null, "TickManager.TicksAbs getter no longer exists");
    }

    [Test]
    public void TickManager_CurTimeSpeed_SetterExists()
    {
        var type = GetType(_game, "Verse.TickManager");
        Assert.That(type, Is.Not.Null);
        var setter = type!.Properties.SingleOrDefault(p => p.Name == "CurTimeSpeed")?.SetMethod;
        Assert.That(setter, Is.Not.Null, "TickManager.CurTimeSpeed setter no longer exists — RunFastForward can't speed up time");
    }

    [Test]
    public void TickManager_CurTimeSpeed_GetterExists()
    {
        var type = GetType(_game, "Verse.TickManager");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "CurTimeSpeed")?.GetMethod;
        Assert.That(getter, Is.Not.Null,
            "TickManager.CurTimeSpeed getter no longer exists — LiveCommandDriver can't capture/restore the pre-FastForward speed or report it in the heartbeat");
    }

    [Test]
    public void TimeSpeed_SuperfastMemberExists()
    {
        var type = GetType(_game, "Verse.TimeSpeed");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "Superfast");
        Assert.That(field, Is.Not.Null, "TimeSpeed.Superfast enum member no longer exists");
    }

    // --- ScreenCapture (RunScreenshot) ---

    [Test]
    public void ScreenCapture_CaptureScreenshot_StringOverloadExists()
    {
        var type = GetType(_screenCapture, "UnityEngine.ScreenCapture");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "CaptureScreenshot" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.String");
        Assert.That(method, Is.Not.Null, "ScreenCapture.CaptureScreenshot(string) no longer exists");
    }

    // --- Live companion channel: native dev-action registry + catalog sources ---
    // These live in the LudeonTK namespace, which is where RimWorld moved its debug tooling after
    // 1.4 — so a game update renaming/moving them is exactly the "silent override breakage" class the
    // repo CLAUDE.md warns about. DevActionCatalog reflects over them and HarnessDebugActions applies
    // the attribute; LiveCommandDriver reads the catalog-source members below.

    [Test]
    public void DebugActionAttribute_ExistsWithFieldsWeRead()
    {
        var type = GetType(_game, "LudeonTK.DebugActionAttribute");
        Assert.That(type, Is.Not.Null, "LudeonTK.DebugActionAttribute no longer exists — DevActionCatalog can't discover native dev-actions and HarnessDebugActions can't register the screenshot dev-action");
        Assert.Multiple(() =>
        {
            foreach (var field in new[] { "name", "category", "actionType", "allowedGameStates" })
                Assert.That(type!.Fields.SingleOrDefault(f => f.Name == field), Is.Not.Null,
                    $"DebugActionAttribute.{field} no longer exists — DevActionCatalog reads it when building the catalog");
            Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "IsAllowedInCurrentGameState")?.GetMethod,
                Is.Not.Null, "DebugActionAttribute.IsAllowedInCurrentGameState getter no longer exists — the catalog's Available flag depends on it");
        });
    }

    [Test]
    public void DebugActionType_EnumMembersExist()
    {
        var type = GetType(_game, "LudeonTK.DebugActionType");
        Assert.That(type, Is.Not.Null, "LudeonTK.DebugActionType no longer exists");
        Assert.Multiple(() =>
        {
            // Action is the only one we invoke; the tool-type members are enumerated (marked
            // not-invokable) so the catalog still needs them to resolve.
            foreach (var member in new[] { "Action", "ToolMap", "ToolMapForPawns", "ToolWorld" })
                Assert.That(type!.Fields.SingleOrDefault(f => f.Name == member), Is.Not.Null,
                    $"DebugActionType.{member} no longer exists");
        });
    }

    [Test]
    public void AllowedGameStates_PlayingOnMapExists()
    {
        var type = GetType(_game, "LudeonTK.AllowedGameStates");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Fields.SingleOrDefault(f => f.Name == "PlayingOnMap"), Is.Not.Null,
            "AllowedGameStates.PlayingOnMap no longer exists — HarnessDebugActions' [DebugAction] gates on it");
    }

    [Test]
    public void GenTypes_AllTypes_GetterExists()
    {
        var type = GetType(_game, "Verse.GenTypes");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "AllTypes")?.GetMethod, Is.Not.Null,
            "Verse.GenTypes.AllTypes no longer exists — DevActionCatalog scans it to find [DebugAction] methods");
    }

    [Test]
    public void GenFilePaths_ScreenshotFolderPath_GetterExists()
    {
        var type = GetType(_game, "Verse.GenFilePaths");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "ScreenshotFolderPath")?.GetMethod, Is.Not.Null,
            "Verse.GenFilePaths.ScreenshotFolderPath no longer exists — the screenshot [DebugAction]'s default output path depends on it");
    }

    [Test]
    public void LoadedModManager_RunningModsListForReading_GetterExists()
    {
        var type = GetType(_game, "Verse.LoadedModManager");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "RunningModsListForReading")?.GetMethod, Is.Not.Null,
            "Verse.LoadedModManager.RunningModsListForReading no longer exists — the catalog's loaded-mods list depends on it");
    }

    [Test]
    public void ModContentPack_PackageId_GetterExists()
    {
        var type = GetType(_game, "Verse.ModContentPack");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "PackageId")?.GetMethod, Is.Not.Null,
            "Verse.ModContentPack.PackageId no longer exists — the catalog reports it per loaded mod");
    }

    [Test]
    public void Map_uniqueID_FieldExists()
    {
        var type = GetType(_game, "Verse.Map");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Fields.SingleOrDefault(f => f.Name == "uniqueID"), Is.Not.Null,
            "Verse.Map.uniqueID no longer exists — LiveCommandDriver uses it to detect a map change and re-emit the catalog");
    }

    [Test]
    public void Map_Parent_GetterExists()
    {
        var type = GetType(_game, "Verse.Map");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "Parent")?.GetMethod, Is.Not.Null,
            "Verse.Map.Parent no longer exists — the catalog's map name comes from map.Parent.LabelCap");
    }

    // --- Screenshot mode (HarnessDebugActions.SetScreenshotMode) ---
    //
    // Worth pinning even though it's "only cosmetic": if screenshotMode silently stopped existing
    // or stopped being settable, every harness screenshot and every timelapse frame would quietly
    // come back with the dev toolbar and HUD painted over the map. Nothing would fail — the gate is
    // numeric — so the visual channel would rot without anyone noticing.

    [Test]
    public void UIRoot_ScreenshotModeField_Exists()
    {
        var type = GetType(_game, "Verse.UIRoot");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "screenshotMode");
        Assert.That(field, Is.Not.Null,
            "Verse.UIRoot.screenshotMode no longer exists — harness screenshots would include the full UI");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.ScreenshotModeHandler"),
            "UIRoot.screenshotMode changed type — HarnessDebugActions.SetScreenshotMode would stop compiling");
    }

    [Test]
    public void ScreenshotModeHandler_Active_SetterExists()
    {
        var type = GetType(_game, "Verse.ScreenshotModeHandler");
        Assert.That(type, Is.Not.Null);
        var setter = type!.Properties.SingleOrDefault(p => p.Name == "Active")?.SetMethod;
        Assert.That(setter, Is.Not.Null,
            "ScreenshotModeHandler.Active setter no longer exists — the harness can't hide the UI for captures");
    }

    // The suppression itself is driven by FiltersCurrentEvent, which is what the vanilla UI roots
    // consult before drawing the dev toolbar, main buttons, alerts and colonist bar. If this went
    // away, setting Active would still compile and would silently do nothing.
    [Test]
    public void ScreenshotModeHandler_FiltersCurrentEvent_GetterExists()
    {
        var type = GetType(_game, "Verse.ScreenshotModeHandler");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "FiltersCurrentEvent")?.GetMethod;
        Assert.That(getter, Is.Not.Null,
            "ScreenshotModeHandler.FiltersCurrentEvent no longer exists — screenshot mode would stop suppressing UI drawing");
    }

    [Test]
    public void Find_UIRoot_GetterExists()
    {
        var type = GetType(_game, "Verse.Find");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "UIRoot")?.GetMethod;
        Assert.That(getter, Is.Not.Null, "Find.UIRoot getter no longer exists");
    }

    // --- Scene setup (Mod/SceneBuilder.cs) ---
    //
    // The spawn/terrain/camera surface that turns a SceneLayout plan into actual geometry. Pinning
    // this is what buys the runtime-spawn approach its advantage over hand-authoring the save XML: if
    // Ludeon moves any of it, these tests fail loudly at build time instead of a scenario quietly
    // placing nothing and screenshotting empty ground.

    // The two trailing bool parameters (respawningAfterLoad, forbidLeavings) are optional, so
    // SceneBuilder's four-argument call site binds to this seven-parameter overload. Pinned at full
    // arity because that is what the compiler actually resolves.
    private static MethodDefinition? ThingSpawnOverload(TypeDefinition genSpawn) =>
        genSpawn.Methods.SingleOrDefault(m =>
            m.Name == "Spawn" &&
            m.Parameters.Count == 7 &&
            m.Parameters[0].ParameterType.FullName == "Verse.Thing" &&
            m.Parameters[1].ParameterType.FullName == "Verse.IntVec3" &&
            m.Parameters[2].ParameterType.FullName == "Verse.Map" &&
            m.Parameters[3].ParameterType.FullName == "Verse.Rot4" &&
            m.Parameters[4].ParameterType.FullName == "Verse.WipeMode" &&
            m.Parameters[5].ParameterType.FullName == "System.Boolean" &&
            m.Parameters[6].ParameterType.FullName == "System.Boolean");

    [Test]
    public void GenSpawn_Spawn_ThingRotWipeModeOverloadExists()
    {
        var type = GetType(_game, "Verse.GenSpawn");
        Assert.That(type, Is.Not.Null);
        Assert.That(ThingSpawnOverload(type!), Is.Not.Null,
            "GenSpawn.Spawn(Thing, IntVec3, Map, Rot4, WipeMode, bool, bool) no longer exists — SceneBuilder can't place things");
    }

    // SceneBuilder treats a null return as "this cell refused the thing" and reports the shortfall.
    // A return-type change would break that detection silently.
    [Test]
    public void GenSpawn_Spawn_ReturnsThing()
    {
        var type = GetType(_game, "Verse.GenSpawn");
        Assert.That(type, Is.Not.Null);
        Assert.That(ThingSpawnOverload(type!)?.ReturnType.FullName, Is.EqualTo("Verse.Thing"),
            "GenSpawn.Spawn no longer returns Thing — SceneBuilder's refused-cell detection relies on a null return");
    }

    // SceneBuilder checks this explicitly, because the Thing overload of Spawn (unlike the ThingDef
    // one) never consults it — without this call a wall asked to stand in deep water would be
    // reported as successfully placed.
    [Test]
    public void GenSpawn_CanSpawnAt_Exists()
    {
        var type = GetType(_game, "Verse.GenSpawn");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "CanSpawnAt" &&
            m.Parameters.Count == 5 &&
            m.Parameters[0].ParameterType.FullName == "Verse.ThingDef" &&
            m.Parameters[1].ParameterType.FullName == "Verse.IntVec3" &&
            m.Parameters[2].ParameterType.FullName == "Verse.Map" &&
            m.Parameters[3].ParameterType.FullName == "System.Nullable`1<Verse.Rot4>" &&
            m.Parameters[4].ParameterType.FullName == "System.Boolean");
        Assert.That(method, Is.Not.Null,
            "GenSpawn.CanSpawnAt(ThingDef, IntVec3, Map, Rot4?, bool) no longer exists — SceneBuilder could no longer tell a refused cell from a placed one");
    }

    [Test]
    public void WipeMode_VanishMemberExists()
    {
        var type = GetType(_game, "Verse.WipeMode");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "Vanish");
        Assert.That(field, Is.Not.Null, "Verse.WipeMode.Vanish no longer exists — SceneBuilder's spawn call won't compile");
    }

    [Test]
    public void ThingMaker_MakeThing_Exists()
    {
        var type = GetType(_game, "Verse.ThingMaker");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "MakeThing" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "Verse.ThingDef" &&
            m.Parameters[1].ParameterType.FullName == "Verse.ThingDef");
        Assert.That(method, Is.Not.Null,
            "ThingMaker.MakeThing(ThingDef, ThingDef) no longer exists — SceneBuilder can't build a stuffed thing");
    }

    [Test]
    public void GenStuff_DefaultStuffFor_Exists()
    {
        var type = GetType(_game, "RimWorld.GenStuff");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "DefaultStuffFor" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "Verse.BuildableDef");
        Assert.That(method, Is.Not.Null,
            "GenStuff.DefaultStuffFor(BuildableDef) no longer exists — SceneBuilder can't pick stuff for a MadeFromStuff def");
    }

    // errorOnFail: false is what keeps an unknown def out of Player.log and in the run's report, so
    // the two-arg overload specifically has to survive.
    [Test]
    public void DefDatabase_GetNamed_HasErrorOnFailOverload()
    {
        var type = GetType(_game, "Verse.DefDatabase`1");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GetNamed" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "System.String" &&
            m.Parameters[1].ParameterType.FullName == "System.Boolean");
        Assert.That(method, Is.Not.Null,
            "DefDatabase<T>.GetNamed(string, bool) no longer exists — SceneBuilder would have to let def lookups Log.Error");
    }

    // SceneBuilder reads both off a ThingDef, but MadeFromStuff is declared one level up on
    // BuildableDef and reached by inheritance — so each is pinned where it actually lives, or a
    // rename there would slip past a ThingDef-only check.
    [Test]
    public void ThingDef_StuffMembersExist()
    {
        var thingDef = GetType(_game, "Verse.ThingDef");
        var buildableDef = GetType(_game, "Verse.BuildableDef");
        Assert.That(thingDef, Is.Not.Null);
        Assert.That(buildableDef, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(buildableDef!.Properties.SingleOrDefault(p => p.Name == "MadeFromStuff")?.GetMethod,
                Is.Not.Null,
                "BuildableDef.MadeFromStuff no longer exists — SceneBuilder can't tell whether a def needs stuff");
            Assert.That(thingDef!.Properties.SingleOrDefault(p => p.Name == "IsStuff")?.GetMethod, Is.Not.Null,
                "ThingDef.IsStuff no longer exists — SceneBuilder can't reject a non-stuff stuff arg");
        });
    }

    [Test]
    public void Rot4_FromString_Exists()
    {
        var type = GetType(_game, "Verse.Rot4");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "FromString" && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.String");
        Assert.That(method, Is.Not.Null,
            "Rot4.FromString(string) no longer exists — SceneLayout validates rotation names against it");
    }

    [Test]
    public void GenGrid_InBounds_MapOverloadExists()
    {
        var type = GetType(_game, "Verse.GenGrid");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "InBounds" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "Verse.IntVec3" &&
            m.Parameters[1].ParameterType.FullName == "Verse.Map");
        Assert.That(method, Is.Not.Null,
            "GenGrid.InBounds(IntVec3, Map) no longer exists — SceneBuilder can't bounds-check placements");
    }

    [Test]
    public void Map_CenterTerrainGridAndFogGridExist()
    {
        var type = GetType(_game, "Verse.Map");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "Center")?.GetMethod, Is.Not.Null,
                "Map.Center no longer exists — SceneLayout's default \"center\" anchor can't resolve");
            var terrainGrid = type.Fields.SingleOrDefault(f => f.Name == "terrainGrid");
            Assert.That(terrainGrid, Is.Not.Null, "Map.terrainGrid no longer exists — SetTerrain can't paint");
            Assert.That(terrainGrid!.FieldType.FullName, Is.EqualTo("Verse.TerrainGrid"),
                "Map.terrainGrid changed type — SceneBuilder.PaintTerrain would stop compiling");
            var fogGrid = type.Fields.SingleOrDefault(f => f.Name == "fogGrid");
            Assert.That(fogGrid, Is.Not.Null, "Map.fogGrid no longer exists — scene setup can't lift fog");
            Assert.That(fogGrid!.FieldType.FullName, Is.EqualTo("Verse.FogGrid"),
                "Map.fogGrid changed type — SceneBuilder.Unfog would stop compiling");
        });
    }

    // Without this the scene is built correctly and is completely invisible on a freshly generated
    // colony, because RimWorld draws neither terrain nor things in fogged cells.
    [Test]
    public void FogGrid_ClearAllFog_Exists()
    {
        var type = GetType(_game, "Verse.FogGrid");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "ClearAllFog" && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null,
            "FogGrid.ClearAllFog() no longer exists — SceneBuilder.Unfog can't reveal the built scene");
    }

    // --- Driver readiness gate (ScenarioDriver.ReadyToRun / LiveCommandDriver.Tick) ---
    //
    // A non-null CurrentMap is not enough: it goes non-null partway through InitNewGame/LoadGame, and
    // stepping in that window corrupted tick state and produced a false-pass run. ProgramState.Playing
    // is the real signal, set by Game.FinalizeInit.

    [Test]
    public void Current_ProgramState_GetterExists()
    {
        var type = GetType(_game, "Verse.Current");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "ProgramState")?.GetMethod;
        Assert.That(getter, Is.Not.Null,
            "Current.ProgramState no longer exists — the driver readiness gate can't tell a half-initialized game from a playable one");
    }

    [Test]
    public void ProgramState_PlayingMemberExists()
    {
        var type = GetType(_game, "Verse.ProgramState");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Fields.SingleOrDefault(f => f.Name == "Playing"), Is.Not.Null,
            "Verse.ProgramState.Playing no longer exists — the driver readiness gate won't compile");
    }

    [Test]
    public void Game_FinalizeInit_Exists()
    {
        var type = GetType(_game, "Verse.Game");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "FinalizeInit" && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null,
            "Game.FinalizeInit() no longer exists — it is what sets ProgramState.Playing, which the readiness gate waits for");
    }

    // --- Scene clearing (Mod/SceneBuilder.cs's PrepareCell / DestroyThingsIn / StripRoof) ---
    //
    // The `clear` arg's whole job is removing what shouldn't be in a scene's footprint, and every
    // member below fails QUIETLY if it moves: a missing roof strip leaves a darkened pad that still
    // screenshots and still passes, and a missing category/destroyable read would make the policy in
    // Shared/SceneClearing.cs classify every thing the same way.

    // Snow is the one ground state a scenario cannot reach through gameplay levers — weather only
    // starts flakes falling, and depth then accrues as a function of temperature and elapsed ticks.
    // The SetSnow step writes the grid directly, so if either member below moves, the step silently
    // becomes "photograph bare ground under a snow-titled scenario".
    [Test]
    public void Map_snowGrid_FieldExists()
    {
        var type = GetType(_game, "Verse.Map");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "snowGrid");
        Assert.That(field, Is.Not.Null,
            "Map.snowGrid no longer exists — the SetSnow step can't lay snow, and snowy scenes become unfilmable again");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.SnowGrid"),
            "Map.snowGrid changed type — SceneBuilder.LaySnow would stop compiling");
    }

    // SetDepth rather than AddDepth: the step is declarative ("this rect is under 0.6 of snow"),
    // and it relies on SetDepth's own clamping, category recompute and mesh dirtying.
    [Test]
    public void SnowGrid_SetDepthAndMaxDepth_Exist()
    {
        var type = GetType(_game, "Verse.SnowGrid");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(
                type!.Methods.SingleOrDefault(m =>
                    m.Name == "SetDepth" && m.Parameters.Count == 2 &&
                    m.Parameters[0].ParameterType.FullName == "Verse.IntVec3" &&
                    m.Parameters[1].ParameterType.FullName == "System.Single"),
                Is.Not.Null,
                "SnowGrid.SetDepth(IntVec3, float) no longer exists — the SetSnow step can't work");
            Assert.That(
                type!.Fields.SingleOrDefault(f => f.Name == "MaxDepth"),
                Is.Not.Null,
                "SnowGrid.MaxDepth no longer exists — SetSnow's documented 0..1 depth range is no longer anchored to vanilla's own maximum");
        });
    }

    // Sand is Odyssey's desert sibling of snow, same unreachable-through-gameplay story: nothing in
    // vanilla accrues sand depth on a timescale a scenario can afford to wait out. The SetSand step
    // writes the grid directly, so if either member below moves, the step silently becomes
    // "photograph bare ground under a sand-titled scenario". Runs whether or not Odyssey is
    // installed — this checks the API, not the licence; SetSandAction's own Skip-without-Odyssey
    // branch is what handles a box that lacks the DLC.
    [Test]
    public void Map_sandGrid_FieldExists()
    {
        var type = GetType(_game, "Verse.Map");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "sandGrid");
        Assert.That(field, Is.Not.Null,
            "Map.sandGrid no longer exists — the SetSand step can't lay sand, and desert scenes become unfilmable again");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.SandGrid"),
            "Map.sandGrid changed type — SceneBuilder.LaySand would stop compiling");
    }

    // SetDepth rather than AddDepth: the step is declarative ("this rect is under 0.6 of sand"), and
    // it relies on SetDepth's own clamping, category recompute and mesh dirtying — same call as
    // SnowGrid's above.
    [Test]
    public void SandGrid_SetDepthAndMaxDepth_Exist()
    {
        var type = GetType(_game, "Verse.SandGrid");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(
                type!.Methods.SingleOrDefault(m =>
                    m.Name == "SetDepth" && m.Parameters.Count == 2 &&
                    m.Parameters[0].ParameterType.FullName == "Verse.IntVec3" &&
                    m.Parameters[1].ParameterType.FullName == "System.Single"),
                Is.Not.Null,
                "SandGrid.SetDepth(IntVec3, float) no longer exists — the SetSand step can't work");
            Assert.That(
                type!.Fields.SingleOrDefault(f => f.Name == "MaxDepth"),
                Is.Not.Null,
                "SandGrid.MaxDepth no longer exists — SetSand's documented 0..1 depth range is no longer anchored to vanilla's own maximum");
        });
    }

    [Test]
    public void Map_roofGrid_FieldExists()
    {
        var type = GetType(_game, "Verse.Map");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "roofGrid");
        Assert.That(field, Is.Not.Null,
            "Map.roofGrid no longer exists — SceneBuilder.StripRoof can't clear overhead mountain roof, and a roofed pad is darkened for exactly the lighting a scenario photographs");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("Verse.RoofGrid"),
            "Map.roofGrid changed type — SceneBuilder's roof clearing would stop compiling");
    }

    // SetRoof(cell, null) is the direct write; SceneBuilder deliberately bypasses vanilla's
    // collapse-checked removal path, so this exact overload is what it depends on.
    [Test]
    public void RoofGrid_SetRoofAndRoofAt_Exist()
    {
        var type = GetType(_game, "Verse.RoofGrid");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(
                type!.Methods.SingleOrDefault(m =>
                    m.Name == "SetRoof" && m.Parameters.Count == 2 &&
                    m.Parameters[0].ParameterType.FullName == "Verse.IntVec3" &&
                    m.Parameters[1].ParameterType.FullName == "Verse.RoofDef"),
                Is.Not.Null,
                "RoofGrid.SetRoof(IntVec3, RoofDef) no longer exists — SceneBuilder.StripRoof can't clear roof");
            var roofAt = type!.Methods.SingleOrDefault(m =>
                m.Name == "RoofAt" && m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "Verse.IntVec3");
            Assert.That(roofAt, Is.Not.Null,
                "RoofGrid.RoofAt(IntVec3) no longer exists — SceneBuilder can't count roofed footprint cells, so the roofed-footprint warning goes silent");
            Assert.That(roofAt!.ReturnType.FullName, Is.EqualTo("Verse.RoofDef"),
                "RoofGrid.RoofAt(IntVec3) no longer returns RoofDef — SceneBuilder's null test for 'unroofed' would stop meaning that");
        });
    }

    [Test]
    public void GridsUtility_GetThingList_Exists()
    {
        var type = GetType(_game, "Verse.GridsUtility");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GetThingList" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "Verse.IntVec3" &&
            m.Parameters[1].ParameterType.FullName == "Verse.Map");
        Assert.That(method, Is.Not.Null,
            "GridsUtility.GetThingList(IntVec3, Map) no longer exists — SceneBuilder can't enumerate what stands in a footprint cell");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("System.Collections.Generic.List`1<Verse.Thing>"),
            "GetThingList no longer returns List<Thing> — SceneBuilder snapshots that list before destroying, because Destroy mutates the live thingGrid list it hands back");
    }

    [Test]
    public void Thing_DestroyAndDestroyed_Exist()
    {
        var type = GetType(_game, "Verse.Thing");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(
                type!.Methods.SingleOrDefault(m =>
                    m.Name == "Destroy" && m.Parameters.Count == 1 &&
                    m.Parameters[0].ParameterType.FullName == "Verse.DestroyMode"),
                Is.Not.Null,
                "Thing.Destroy(DestroyMode) no longer exists — scene clearing can't remove rock, plants or buildings from a footprint");
            // Checked before every destroy: a multi-cell building destroyed while clearing an earlier
            // cell is still in the next cell's snapshot, and Destroy Log.Errors on an already-destroyed
            // thing. Without this the run would spew errors it never reports.
            Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "Destroyed")?.GetMethod, Is.Not.Null,
                "Thing.Destroyed no longer exists — SceneBuilder would double-destroy multi-cell things and Log.Error doing it");
        });
    }

    [Test]
    public void DestroyMode_VanishMemberExists()
    {
        var type = GetType(_game, "Verse.DestroyMode");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Fields.SingleOrDefault(f => f.Name == "Vanish"), Is.Not.Null,
            "Verse.DestroyMode.Vanish no longer exists — clearing would have to use a mode that drops leavings, putting fresh chunks into the footprint it just cleared");
    }

    // Shared/SceneClearing.cs decides what may be destroyed from these two members alone, so a rename
    // of either would silently collapse the whole policy into one branch.
    [Test]
    public void ThingDef_CategoryAndDestroyable_Exist()
    {
        var type = GetType(_game, "Verse.ThingDef");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            var category = type!.Fields.SingleOrDefault(f => f.Name == "category");
            Assert.That(category, Is.Not.Null,
                "ThingDef.category no longer exists — SceneClearing.Classify can't tell a rock wall from a pawn");
            Assert.That(category!.FieldType.FullName, Is.EqualTo("Verse.ThingCategory"),
                "ThingDef.category changed type — SceneBuilder passes its ToString() to SceneClearing's category table");
            var destroyable = type.Fields.SingleOrDefault(f => f.Name == "destroyable");
            Assert.That(destroyable, Is.Not.Null,
                "ThingDef.destroyable no longer exists — clearing would call Destroy on non-destroyable things, which Log.Errors instead of reporting a blocker");
            Assert.That(destroyable!.FieldType.FullName, Is.EqualTo("System.Boolean"),
                "ThingDef.destroyable is no longer a bool — SceneClearing.Classify's second argument would stop binding");
        });
    }

    // SceneClearing keeps its clearable-category whitelist as the enum's member NAMES, so the adapter
    // does no branching and can't drift from the table. That trade only holds if the names hold: a
    // rename would turn the renamed category into "leave alone" and clearing would quietly stop
    // clearing it.
    [Test]
    public void ThingCategory_MembersNamedBySceneClearingExist()
    {
        var type = GetType(_game, "Verse.ThingCategory");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            foreach (var member in new[] { "Pawn", "Building", "Plant", "Item", "Filth" })
                Assert.That(type!.Fields.SingleOrDefault(f => f.Name == member), Is.Not.Null,
                    $"ThingCategory.{member} no longer exists — Shared/SceneClearing.cs names it as a string, so clearing would silently spare that category");
        });
    }

    // Named in a refused-cell report, so "(128,118) refused" becomes something an author can act on.
    [Test]
    public void TerrainGrid_TerrainAt_CellOverloadExists()
    {
        var type = GetType(_game, "Verse.TerrainGrid");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "TerrainAt" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "Verse.IntVec3");
        Assert.That(method, Is.Not.Null,
            "TerrainGrid.TerrainAt(IntVec3) no longer exists — SceneBuilder can't name the terrain that refused a placement");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("Verse.TerrainDef"),
            "TerrainGrid.TerrainAt(IntVec3) no longer returns TerrainDef — the refusal message's defName read would stop compiling");
    }

    [Test]
    public void TerrainGrid_SetTerrain_Exists()
    {
        var type = GetType(_game, "Verse.TerrainGrid");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "SetTerrain" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "Verse.IntVec3" &&
            m.Parameters[1].ParameterType.FullName == "Verse.TerrainDef");
        Assert.That(method, Is.Not.Null,
            "TerrainGrid.SetTerrain(IntVec3, TerrainDef) no longer exists — the SetTerrain step can't work");
    }

    [Test]
    public void CellRect_CenteredOn_WidthHeightOverloadExists()
    {
        var type = GetType(_game, "Verse.CellRect");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "CenteredOn" &&
            m.Parameters.Count == 3 &&
            m.Parameters[0].ParameterType.FullName == "Verse.IntVec3" &&
            m.Parameters[1].ParameterType.FullName == "System.Int32" &&
            m.Parameters[2].ParameterType.FullName == "System.Int32");
        Assert.That(method, Is.Not.Null,
            "CellRect.CenteredOn(IntVec3, int, int) no longer exists — SceneBuilder.PaintTerrain builds its rect with it");
    }

    // JumpToCurrentMapLoc rather than PanToMapLoc: the pan animates over several frames, which a
    // scenario would then have to wait out before screenshotting.
    [Test]
    public void CameraDriver_JumpAndZoomMembersExist()
    {
        var type = GetType(_game, "Verse.CameraDriver");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(
                type!.Methods.SingleOrDefault(m =>
                    m.Name == "JumpToCurrentMapLoc" && m.Parameters.Count == 1 &&
                    m.Parameters[0].ParameterType.FullName == "Verse.IntVec3"),
                Is.Not.Null,
                "CameraDriver.JumpToCurrentMapLoc(IntVec3) no longer exists — the LookAt step can't aim the camera");
            Assert.That(
                type.Methods.SingleOrDefault(m =>
                    m.Name == "SetRootSize" && m.Parameters.Count == 1 &&
                    m.Parameters[0].ParameterType.FullName == "System.Single"),
                Is.Not.Null,
                "CameraDriver.SetRootSize(float) no longer exists — LookAt's zoom arg can't apply");
        });
    }

    [Test]
    public void Find_CameraDriver_GetterExists()
    {
        var type = GetType(_game, "Verse.Find");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "CameraDriver")?.GetMethod;
        Assert.That(getter, Is.Not.Null, "Find.CameraDriver getter no longer exists — LookAt can't reach the camera");
    }

    // --- Mid-session save reload (Mod/FixtureReloader.cs) ---
    //
    // Running several scenarios in one game load reloads the save between the ones that mutated the
    // map, because nothing else can undo a WipeMode.Vanish spawn or a repainted terrain grid. That is
    // exactly what vanilla's own in-game Load Game does, and the whole chain matters: LoadGame(string)
    // queues a long event that installs a fresh Game with InitData.gameToLoad set and reloads the
    // "Play" scene, where Root_Play.Start() picks gameToLoad up. See DESIGN.md, "Batching scenarios
    // into one load".

    [Test]
    public void GameDataSaveLoader_LoadGame_StringOverloadExists()
    {
        var type = GetType(_game, "Verse.GameDataSaveLoader");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "LoadGame" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.String");
        Assert.That(method, Is.Not.Null,
            "GameDataSaveLoader.LoadGame(string) no longer exists — Mod/FixtureReloader.cs can't reload the fixture between scenarios");
    }

    [Test]
    public void GenFilePaths_FilePathForSavedGame_Exists()
    {
        var type = GetType(_game, "Verse.GenFilePaths");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "FilePathForSavedGame" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.String");
        Assert.That(method, Is.Not.Null,
            "GenFilePaths.FilePathForSavedGame(string) no longer exists — Mod/FixtureReloader.cs can't pre-check that the save it is about to reload exists, so a missing save would surface as a timeout instead of an error");
    }

    // Whether the reload actually happened is decided by Current.Game's identity changing: for a frame
    // after LoadGame the queued long event isn't current yet, so ProgramState/CurrentMap still describe
    // the pre-reload world. Without a settable Current.Game there is nothing for vanilla to replace and
    // that postcondition would be meaningless.
    [Test]
    public void Current_Game_GetterAndSetterExist()
    {
        var type = GetType(_game, "Verse.Current");
        Assert.That(type, Is.Not.Null);
        var property = type!.Properties.SingleOrDefault(p => p.Name == "Game");
        Assert.Multiple(() =>
        {
            Assert.That(property?.GetMethod, Is.Not.Null,
                "Current.Game getter no longer exists — ScenarioDriver.ReloadFinished can't tell a finished reload from one that never started");
            Assert.That(property?.SetMethod, Is.Not.Null,
                "Current.Game setter no longer exists — vanilla's load path replaced the Game instance through it, which is the signal ScenarioDriver.ReloadFinished watches for");
        });
    }

    // Root_Play.Start()'s gameToLoad branch is what actually performs a mid-session reload after the
    // scene reloads. We never call it, but the reload does nothing without it.
    [Test]
    public void SavedGameLoaderNow_LoadGameFromSaveFileNow_Exists()
    {
        var type = GetType(_game, "Verse.SavedGameLoaderNow");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "LoadGameFromSaveFileNow" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.String");
        Assert.That(method, Is.Not.Null,
            "SavedGameLoaderNow.LoadGameFromSaveFileNow(string) no longer exists — the reloaded Play scene would have nothing to load the save with");
    }

    // Game.LoadGame is what sets ProgramState.MapInitializing on the reload path, which is what makes
    // the readiness gate re-arm per load rather than reporting the pre-reload world as ready.
    [Test]
    public void Game_LoadGame_Exists()
    {
        var type = GetType(_game, "Verse.Game");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m => m.Name == "LoadGame" && m.Parameters.Count == 0);
        Assert.That(method, Is.Not.Null,
            "Game.LoadGame() no longer exists — the mid-suite reload path is gone");
    }

    // --- Soft reset between scenarios (Mod/WorldStateReset.cs) ---
    //
    // Reading the camera back is new: LookAt only ever set it before, so nothing needed a getter. A
    // suite has to restore it between scenarios that moved it.

    [Test]
    public void CameraDriver_ReadbackMembersExist()
    {
        var type = GetType(_game, "Verse.CameraDriver");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "RootSize")?.GetMethod, Is.Not.Null,
                "CameraDriver.RootSize getter no longer exists — WorldStateReset can't record the zoom to restore between scenarios");
            Assert.That(type.Properties.SingleOrDefault(p => p.Name == "MapPosition")?.GetMethod, Is.Not.Null,
                "CameraDriver.MapPosition getter no longer exists — WorldStateReset can't record the camera cell to restore between scenarios");
        });
    }

    // --- Weather (SetWeather step) ---

    [Test]
    public void Map_WeatherManager_FieldExists()
    {
        var type = GetType(_game, "Verse.Map");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "weatherManager");
        Assert.That(field, Is.Not.Null, "Map.weatherManager no longer exists — SetWeather can't reach the weather");
    }

    [Test]
    public void WeatherManager_TransitionTo_SignatureExists()
    {
        var type = GetType(_game, "RimWorld.WeatherManager");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "TransitionTo" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "Verse.WeatherDef");
        Assert.That(method, Is.Not.Null, "WeatherManager.TransitionTo(WeatherDef) no longer exists");
    }

    [Test]
    public void WeatherManager_CurWeatherAge_FieldExists()
    {
        var type = GetType(_game, "RimWorld.WeatherManager");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "curWeatherAge");
        Assert.That(field, Is.Not.Null,
            "WeatherManager.curWeatherAge no longer exists — SetWeather can't skip the blend, so a " +
            "screenshot right after it would catch a half-mixed sky");
    }

    [Test]
    public void WeatherManager_TransitionTicks_StillMatchesHardcodedValue()
    {
        var type = GetType(_game, "RimWorld.WeatherManager");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "TransitionTicks");
        Assert.That(field, Is.Not.Null, "WeatherManager.TransitionTicks no longer exists");
        Assert.That(field!.Constant, Is.EqualTo(4000f),
            "WeatherManager.TransitionTicks changed — SetWeather ages the transition to exactly this " +
            "value to complete the blend, so a different one would leave a half-mixed sky");
    }

    // --- GameConditions (StartCondition step) ---
    //
    // These shipped with StartCondition but were never covered here, so a rename would have been
    // caught only by a live run failing at the step.

    [Test]
    public void Map_GameConditionManager_FieldExists()
    {
        var type = GetType(_game, "Verse.Map");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "gameConditionManager");
        Assert.That(field, Is.Not.Null, "Map.gameConditionManager no longer exists — StartCondition can't register a condition");
    }

    [Test]
    public void GameConditionManager_RegisterCondition_SignatureExists()
    {
        var type = GetType(_game, "RimWorld.GameConditionManager");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "RegisterCondition" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "RimWorld.GameCondition");
        Assert.That(method, Is.Not.Null, "GameConditionManager.RegisterCondition(GameCondition) no longer exists");
    }

    [Test]
    public void GameCondition_StartTick_FieldExists()
    {
        var type = GetType(_game, "RimWorld.GameCondition");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "startTick");
        Assert.That(field, Is.Not.Null,
            "GameCondition.startTick no longer exists — StartCondition's agedHours can't back-date a condition");
    }

    // --- Log buffer (Assert step's log excerpt) ---
    //
    // AssertAction reads the game's in-memory log rather than Player.log on disk, because the file is
    // owned by Unity and written concurrently. That makes these members load-bearing for the vision
    // tier: without them a review packet silently ships with no log evidence at all.

    [Test]
    public void Log_Messages_GetterExists()
    {
        var type = GetType(_game, "Verse.Log");
        Assert.That(type, Is.Not.Null);
        var getter = type!.Properties.SingleOrDefault(p => p.Name == "Messages")?.GetMethod;
        Assert.That(getter, Is.Not.Null, "Verse.Log.Messages no longer exists — Assert can't capture a log excerpt");
    }

    [Test]
    public void LogMessage_TextTypeAndRepeats_FieldsExist()
    {
        var type = GetType(_game, "Verse.LogMessage");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(type!.Fields.SingleOrDefault(f => f.Name == "text"), Is.Not.Null,
                "LogMessage.text no longer exists");
            Assert.That(type.Fields.SingleOrDefault(f => f.Name == "type"), Is.Not.Null,
                "LogMessage.type no longer exists — Assert can't filter to warnings and errors");
            Assert.That(type.Fields.SingleOrDefault(f => f.Name == "repeats"), Is.Not.Null,
                "LogMessage.repeats no longer exists");
        });
    }

    [Test]
    public void LogMessageType_StillHasMessageWarningError()
    {
        var type = GetType(_game, "Verse.LogMessageType");
        Assert.That(type, Is.Not.Null);
        var names = type!.Fields.Where(f => f.IsStatic).Select(f => f.Name).ToList();
        Assert.That(names, Is.SupersetOf(new[] { "Message", "Warning", "Error" }),
            "LogMessageType's members changed — Assert's warning/error filter would silently misclassify");
    }

    // --- Pawn spawning (Mod/SceneBuilder.cs's SpawnPawns) ---
    //
    // The SpawnPawn step generates a pawn rather than building a thing, and assigns it a faction,
    // gender and hediffs, so it leans on a wide slice of vanilla the rest of scene setup never touches.
    // If Ludeon moves any of it, these fail loudly at build time instead of the step silently spawning
    // nothing, or spawning a factionless/condition-free pawn that quietly tests less than it claims.

    // GeneratePawn(PawnGenerationRequest) is the overload SpawnPawns calls (rather than the
    // (kind, faction) convenience) so it can set FixedGender. Both the method and the request's
    // gender field are pinned, because losing either silently drops the gender control.
    [Test]
    public void PawnGenerator_GeneratePawn_RequestOverloadExists()
    {
        var type = GetType(_game, "Verse.PawnGenerator");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GeneratePawn" &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "Verse.PawnGenerationRequest");
        Assert.That(method, Is.Not.Null,
            "PawnGenerator.GeneratePawn(PawnGenerationRequest) no longer exists — SpawnPawns can't generate a pawn");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("Verse.Pawn"),
            "PawnGenerator.GeneratePawn no longer returns Pawn — SpawnPawns' spawn call would stop compiling");
    }

    [Test]
    public void PawnGenerationRequest_KindFactionCtorAndFixedGenderExist()
    {
        var type = GetType(_game, "Verse.PawnGenerationRequest");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            var ctor = type!.Methods.SingleOrDefault(m =>
                m.IsConstructor &&
                m.Parameters.Count >= 2 &&
                m.Parameters[0].ParameterType.FullName == "Verse.PawnKindDef" &&
                m.Parameters[1].ParameterType.FullName == "RimWorld.Faction");
            Assert.That(ctor, Is.Not.Null,
                "PawnGenerationRequest(PawnKindDef, Faction, ...) no longer exists — SpawnPawns can't build a request");
            var fixedGender = type!.Properties.SingleOrDefault(p => p.Name == "FixedGender");
            Assert.That(fixedGender?.SetMethod, Is.Not.Null,
                "PawnGenerationRequest.FixedGender no longer settable — SpawnPawn's gender arg can't apply");
            Assert.That(fixedGender!.PropertyType.FullName, Is.EqualTo("System.Nullable`1<Verse.Gender>"),
                "PawnGenerationRequest.FixedGender changed type — SpawnPawns' ToGender would stop binding");
        });
    }

    [Test]
    public void Gender_MaleAndFemaleMembersExist()
    {
        var type = GetType(_game, "Verse.Gender");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            foreach (var member in new[] { "Male", "Female" })
                Assert.That(type!.Fields.SingleOrDefault(f => f.Name == member), Is.Not.Null,
                    $"Verse.Gender.{member} no longer exists — SpawnPawn's gender arg can't map to it");
        });
    }

    // Faction.OfPlayer (player spawns) and the deterministic enemy-faction walk (hostile spawns).
    [Test]
    public void Faction_OfPlayerAndHostileWalkMembersExist()
    {
        var faction = GetType(_game, "RimWorld.Faction");
        var manager = GetType(_game, "RimWorld.FactionManager");
        Assert.That(faction, Is.Not.Null);
        Assert.That(manager, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(faction!.Properties.SingleOrDefault(p => p.Name == "OfPlayer")?.GetMethod, Is.Not.Null,
                "Faction.OfPlayer no longer exists — SpawnPawn can't give a pawn the player faction");
            Assert.That(manager!.Properties.SingleOrDefault(p => p.Name == "AllFactionsListForReading")?.GetMethod,
                Is.Not.Null,
                "FactionManager.AllFactionsListForReading no longer exists — the deterministic enemy-faction pick can't iterate");
            // The flags FirstEnemyFaction filters on: a rename would silently widen or empty the pick.
            Assert.That(faction!.Fields.SingleOrDefault(f => f.Name == "defeated"), Is.Not.Null,
                "Faction.defeated no longer exists — enemy-faction filter can't skip defeated factions");
            Assert.That(faction!.Fields.SingleOrDefault(f => f.Name == "temporary"), Is.Not.Null,
                "Faction.temporary no longer exists — enemy-faction filter can't skip temporary factions");
            Assert.That(faction!.Properties.SingleOrDefault(p => p.Name == "Hidden")?.GetMethod, Is.Not.Null,
                "Faction.Hidden no longer exists — enemy-faction filter can't skip hidden (mechanoid/insect) factions");
        });
    }

    // HostileTo is an extension method in FactionUtility, not an instance method on Faction, so it is
    // pinned where it actually lives.
    [Test]
    public void FactionUtility_HostileTo_Exists()
    {
        var type = GetType(_game, "RimWorld.FactionUtility");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "HostileTo" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "RimWorld.Faction" &&
            m.Parameters[1].ParameterType.FullName == "RimWorld.Faction");
        Assert.That(method, Is.Not.Null,
            "FactionUtility.HostileTo(Faction, Faction) no longer exists — SpawnPawn can't find an enemy faction");
    }

    // Body-part-targeted hediffs resolve against the kind's shared body: PawnKindDef.RaceProps ->
    // RaceProperties.body (a BodyDef) -> GetPartsWithDef. All three links matter.
    [Test]
    public void BodyResolutionChainExists()
    {
        var pawnKind = GetType(_game, "Verse.PawnKindDef");
        var raceProps = GetType(_game, "Verse.RaceProperties");
        var bodyDef = GetType(_game, "Verse.BodyDef");
        Assert.Multiple(() =>
        {
            Assert.That(pawnKind!.Properties.SingleOrDefault(p => p.Name == "RaceProps")?.GetMethod, Is.Not.Null,
                "PawnKindDef.RaceProps no longer exists — SpawnPawn can't reach the kind's body");
            var body = raceProps!.Fields.SingleOrDefault(f => f.Name == "body");
            Assert.That(body, Is.Not.Null, "RaceProperties.body no longer exists — body-part hediffs can't resolve");
            Assert.That(body!.FieldType.FullName, Is.EqualTo("Verse.BodyDef"),
                "RaceProperties.body changed type — SpawnPawns' GetPartsWithDef call would stop binding");
            var getParts = bodyDef!.Methods.SingleOrDefault(m =>
                m.Name == "GetPartsWithDef" && m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "Verse.BodyPartDef");
            Assert.That(getParts, Is.Not.Null,
                "BodyDef.GetPartsWithDef(BodyPartDef) no longer exists — SpawnPawn can't target a body part");
            Assert.That(getParts!.ReturnType.FullName, Is.EqualTo("System.Collections.Generic.List`1<Verse.BodyPartRecord>"),
                "BodyDef.GetPartsWithDef no longer returns List<BodyPartRecord> — SpawnPawns' FirstOrDefault would stop binding");
        });
    }

    // Applying a hediff: pawn.health (a Pawn_HealthTracker), AddHediff returning the installed Hediff
    // so its Severity can be set.
    [Test]
    public void HediffApplicationMembersExist()
    {
        var pawn = GetType(_game, "Verse.Pawn");
        var health = GetType(_game, "Verse.Pawn_HealthTracker");
        var hediff = GetType(_game, "Verse.Hediff");
        Assert.Multiple(() =>
        {
            var healthField = pawn!.Fields.SingleOrDefault(f => f.Name == "health");
            Assert.That(healthField, Is.Not.Null, "Pawn.health no longer exists — SpawnPawn can't apply hediffs");
            Assert.That(healthField!.FieldType.FullName, Is.EqualTo("Verse.Pawn_HealthTracker"),
                "Pawn.health changed type — SpawnPawns' AddHediff call would stop binding");
            var addHediff = health!.Methods.SingleOrDefault(m =>
                m.Name == "AddHediff" && m.Parameters.Count >= 1 &&
                m.Parameters[0].ParameterType.FullName == "Verse.HediffDef");
            Assert.That(addHediff, Is.Not.Null,
                "Pawn_HealthTracker.AddHediff(HediffDef, ...) no longer exists — SpawnPawn can't apply a hediff");
            Assert.That(addHediff!.ReturnType.FullName, Is.EqualTo("Verse.Hediff"),
                "AddHediff no longer returns Hediff — SpawnPawns can't set the applied hediff's severity");
            Assert.That(hediff!.Properties.SingleOrDefault(p => p.Name == "Severity")?.SetMethod, Is.Not.Null,
                "Hediff.Severity is no longer settable — SpawnPawn's :severity can't apply");
        });
    }

    // GenGrid.Standable(IntVec3, Map) is what SpawnPawns uses instead of GenSpawn.CanSpawnAt (which
    // takes a ThingDef, not a PawnKindDef) to tell a wall or deep-water cell from one a pawn can stand
    // in. Losing it would make every refused cell read as a silent success.
    [Test]
    public void GenGrid_Standable_MapOverloadExists()
    {
        var type = GetType(_game, "Verse.GenGrid");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "Standable" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "Verse.IntVec3" &&
            m.Parameters[1].ParameterType.FullName == "Verse.Map");
        Assert.That(method, Is.Not.Null,
            "GenGrid.Standable(IntVec3, Map) no longer exists — SpawnPawns can't tell a standable cell from a wall");
    }

    // --- LandInOrbit: Odyssey's orbital map-generation path ---
    //
    // Every member below is one this step calls to build a REAL orbital map rather than dress a
    // surface one up as one, so losing any of them silently would leave exactly the fake the step
    // exists to avoid. They live in the base assembly (all DLC code ships there), so these tests run
    // whether or not Odyssey is installed — which is the point: they check the API, not the licence.

    [Test]
    public void PlanetLayerDefOf_OrbitExists()
    {
        var type = GetType(_game, "RimWorld.PlanetLayerDefOf");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "Orbit");
        Assert.That(field, Is.Not.Null,
            "PlanetLayerDefOf.Orbit no longer exists — LandInOrbit can't identify the orbit layer");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("RimWorld.PlanetLayerDef"));
    }

    [Test]
    public void PlanetLayerSettingsDefOf_OrbitExists()
    {
        var type = GetType(_game, "RimWorld.PlanetLayerSettingsDefOf");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Fields.SingleOrDefault(f => f.Name == "Orbit"), Is.Not.Null,
            "PlanetLayerSettingsDefOf.Orbit no longer exists — LandInOrbit can't register an orbit " +
            "layer into a save that predates Odyssey");
        var settingsDef = GetType(_game, "RimWorld.PlanetLayerSettingsDef");
        Assert.That(settingsDef!.Fields.SingleOrDefault(f => f.Name == "settings"), Is.Not.Null,
            "PlanetLayerSettingsDef.settings no longer exists — there is nothing to register the layer with");
    }

    // OrbitLayer.CanSelectLayer is the precondition that made this step necessary in the first place:
    // the layer refuses selection until a world object exists on it. We satisfy it by creating the map
    // parent, so an override that stopped existing would mean the constraint moved somewhere else.
    [Test]
    public void OrbitLayer_CanSelectLayer_OverridesPlanetLayer()
    {
        var orbit = GetType(_game, "RimWorld.OrbitLayer");
        var planetLayer = GetType(_game, "RimWorld.Planet.PlanetLayer");
        Assert.That(orbit, Is.Not.Null, "RimWorld.OrbitLayer no longer exists");
        Assert.Multiple(() =>
        {
            Assert.That(orbit!.BaseType?.FullName, Is.EqualTo("RimWorld.Planet.PlanetLayer"),
                "OrbitLayer no longer derives from PlanetLayer");
            Assert.That(orbit.Methods.SingleOrDefault(m => m.Name == "CanSelectLayer"), Is.Not.Null,
                "OrbitLayer.CanSelectLayer no longer exists — its world-object precondition moved");
            Assert.That(planetLayer!.Methods.SingleOrDefault(
                    m => m.Name == "CanSelectLayer" && m.IsVirtual), Is.Not.Null,
                "PlanetLayer.CanSelectLayer is no longer virtual — OrbitLayer's override is orphaned");
        });
    }

    [Test]
    public void WorldObjects_AnyWorldObjectOnLayer_Exists()
    {
        var type = GetType(_game, "RimWorld.Planet.WorldObjectsHolder");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Methods.SingleOrDefault(m => m.Name == "AnyWorldObjectOnLayer"), Is.Not.Null,
            "WorldObjectsHolder.AnyWorldObjectOnLayer no longer exists — the check OrbitLayer gates " +
            "selection on is gone, so LandInOrbit's map parent may no longer be what unlocks the layer");
    }

    // The layer-side members LandInOrbit reads: the registry it looks the orbit layer up in, and the
    // registration call it falls back to for a save with no orbit layer in it.
    [Test]
    public void WorldGrid_PlanetLayerMembersExist()
    {
        var type = GetType(_game, "RimWorld.Planet.WorldGrid");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "PlanetLayers")?.GetMethod,
                Is.Not.Null, "WorldGrid.PlanetLayers no longer exists — LandInOrbit can't find the orbit layer");
            Assert.That(type.Methods.Any(m =>
                    m.Name == "RegisterPlanetLayer" && m.Parameters.Count >= 2 &&
                    m.Parameters[0].ParameterType.FullName == "RimWorld.PlanetLayerDef" &&
                    m.Parameters[1].ParameterType.FullName == "RimWorld.PlanetLayerSettings"),
                Is.True,
                "WorldGrid.RegisterPlanetLayer(PlanetLayerDef, PlanetLayerSettings, ...) no longer " +
                "exists — a save predating Odyssey can't be given an orbit layer");
        });
    }

    [Test]
    public void PlanetLayer_TileGeometryMembersExist()
    {
        var type = GetType(_game, "RimWorld.Planet.PlanetLayer");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "Def")?.GetMethod, Is.Not.Null,
                "PlanetLayer.Def no longer exists — LandInOrbit can't tell an orbit layer from a surface one");
            Assert.That(type.Properties.SingleOrDefault(p => p.Name == "TilesCount")?.GetMethod, Is.Not.Null,
                "PlanetLayer.TilesCount no longer exists — there is nothing to iterate to find a tile");
            var longLat = type.Methods.SingleOrDefault(m =>
                m.Name == "LongLatOf" && m.Parameters.Count == 1 &&
                m.Parameters[0].ParameterType.FullName == "System.Int32");
            Assert.That(longLat, Is.Not.Null,
                "PlanetLayer.LongLatOf(int) no longer exists — LandInOrbit can't resolve a tile by lat/long");
            Assert.That(longLat!.ReturnType.FullName, Is.EqualTo("UnityEngine.Vector2"));
            Assert.That(type.Methods.Any(m => m.Name == "RunWorldGeneration"), Is.True,
                "PlanetLayer.RunWorldGeneration no longer exists — a freshly registered orbit layer " +
                "would have geometry but no tiles, and every lookup on it would throw");
        });
    }

    // PlanetLayerDef.DefaultBiome/DefaultWorldObject are what make the generated map genuinely
    // orbital: the biome WorldGenStep_Tiles stamps on each tile, and the MapParent def whose own
    // MapGeneratorDef is Odyssey's space generator.
    [Test]
    public void PlanetLayerDef_DefaultsExist()
    {
        var type = GetType(_game, "RimWorld.PlanetLayerDef");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            var worldObject = type!.Properties.SingleOrDefault(p => p.Name == "DefaultWorldObject");
            Assert.That(worldObject?.GetMethod, Is.Not.Null,
                "PlanetLayerDef.DefaultWorldObject no longer exists — LandInOrbit has nothing to hang a map on");
            Assert.That(worldObject!.PropertyType.FullName, Is.EqualTo("RimWorld.WorldObjectDef"));
            Assert.That(type.Properties.SingleOrDefault(p => p.Name == "DefaultBiome")?.GetMethod,
                Is.Not.Null,
                "PlanetLayerDef.DefaultBiome no longer exists — orbit tiles would not be stamped vacuum");
            Assert.That(type.Fields.SingleOrDefault(f => f.Name == "elevationString"), Is.Not.Null,
                "PlanetLayerDef.elevationString no longer exists — the run log can't say how high up it landed");
        });
    }

    // The generation entry point itself — the same call SettleInEmptyTileUtility makes. Using vanilla's
    // own helper (rather than hand-rolling MapParent creation plus MapGenerator.GenerateMap) is what
    // keeps the harness on the real path, so its signature is worth pinning.
    [Test]
    public void GetOrGenerateMapUtility_SizedOverloadExists()
    {
        var type = GetType(_game, "Verse.GetOrGenerateMapUtility");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GetOrGenerateMap" &&
            m.Parameters.Count >= 3 &&
            m.Parameters[0].ParameterType.FullName == "RimWorld.Planet.PlanetTile" &&
            m.Parameters[1].ParameterType.FullName == "Verse.IntVec3" &&
            m.Parameters[2].ParameterType.FullName == "RimWorld.WorldObjectDef");
        Assert.That(method, Is.Not.Null,
            "GetOrGenerateMapUtility.GetOrGenerateMap(PlanetTile, IntVec3, WorldObjectDef, ...) no " +
            "longer exists — LandInOrbit can't generate an orbital map through vanilla's own path");
        Assert.That(method!.ReturnType.FullName, Is.EqualTo("Verse.Map"));
    }

    [Test]
    public void PlanetTile_LayerConstructorAndLayerDefExist()
    {
        var type = GetType(_game, "RimWorld.Planet.PlanetTile");
        Assert.That(type, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(type!.Methods.Any(m =>
                    m.IsConstructor && m.Parameters.Count == 2 &&
                    m.Parameters[0].ParameterType.FullName == "System.Int32" &&
                    m.Parameters[1].ParameterType.FullName == "RimWorld.Planet.PlanetLayer"),
                Is.True,
                "PlanetTile(int, PlanetLayer) no longer exists — LandInOrbit can't name a tile ON the orbit layer");
            Assert.That(type.Properties.SingleOrDefault(p => p.Name == "LayerDef")?.GetMethod, Is.Not.Null,
                "PlanetTile.LayerDef no longer exists — the step can't verify the map it built is in orbit");
            Assert.That(type.Fields.SingleOrDefault(f => f.Name == "Invalid"), Is.Not.Null,
                "PlanetTile.Invalid no longer exists — the step's failure path has no tile to return");
        });
    }

    // The postcondition check. BiomeDef.inVacuum is on the BASE BiomeDef (all DLC code ships in the
    // base assembly), which is what lets the harness verify vacuum without a soft reference.
    [Test]
    public void BiomeDef_InVacuumExists()
    {
        var type = GetType(_game, "RimWorld.BiomeDef");
        Assert.That(type, Is.Not.Null);
        var field = type!.Fields.SingleOrDefault(f => f.Name == "inVacuum");
        Assert.That(field, Is.Not.Null,
            "BiomeDef.inVacuum no longer exists — LandInOrbit can't prove the map it generated is a vacuum map");
        Assert.That(field!.FieldType.FullName, Is.EqualTo("System.Boolean"));
    }

    [Test]
    public void VacuumUtility_GetVacuumExists()
    {
        var type = GetType(_game, "Verse.VacuumUtility");
        Assert.That(type, Is.Not.Null);
        var method = type!.Methods.SingleOrDefault(m =>
            m.Name == "GetVacuum" && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "Verse.IntVec3" &&
            m.Parameters[1].ParameterType.FullName == "Verse.Map");
        Assert.That(method, Is.Not.Null,
            "VacuumUtility.GetVacuum(IntVec3, Map) no longer exists — nothing can check per-cell vacuum " +
            "on a generated orbital map, which is how a dressed-up surface map would be caught");
    }

    [Test]
    public void ModsConfig_OdysseyActiveExists()
    {
        var type = GetType(_game, "Verse.ModsConfig");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Properties.SingleOrDefault(p => p.Name == "OdysseyActive")?.GetMethod,
            Is.Not.Null,
            "ModsConfig.OdysseyActive no longer exists — LandInOrbit can't tell 'no DLC' (skip) from 'broken' (fail)");
    }

    // Switching to the generated map. A settable Game.CurrentMap is what makes every later step in the
    // scenario run against the platform instead of the fixture colony.
    [Test]
    public void Game_CurrentMap_IsSettable()
    {
        var type = GetType(_game, "Verse.Game");
        Assert.That(type, Is.Not.Null);
        var property = type!.Properties.SingleOrDefault(p => p.Name == "CurrentMap");
        Assert.That(property?.SetMethod, Is.Not.Null,
            "Game.CurrentMap is no longer settable — LandInOrbit can generate an orbital map but not move to it");
        Assert.That(type.Methods.Any(m => m.Name == "FindMap"), Is.True,
            "Game.FindMap no longer exists — LandInOrbit can't reuse a platform it already generated");
    }

    [Test]
    public void FogGrid_ClearAllFogExists()
    {
        var type = GetType(_game, "Verse.FogGrid");
        Assert.That(type, Is.Not.Null);
        Assert.That(type!.Methods.SingleOrDefault(m => m.Name == "ClearAllFog"), Is.Not.Null,
            "FogGrid.ClearAllFog no longer exists — a freshly generated orbital map would screenshot black");
    }

    // --- helpers ---

    private static TypeDefinition? GetType(ModuleDefinition module, string fullName) =>
        module.Types.FirstOrDefault(t => t.FullName == fullName);

    private static void AssertConstEquals(TypeDefinition type, string fieldName, int expected)
    {
        var field = type.Fields.SingleOrDefault(f => f.Name == fieldName);
        Assert.That(field, Is.Not.Null, $"GenDate.{fieldName} no longer exists");
        Assert.That(field!.Constant, Is.EqualTo(expected), $"GenDate.{fieldName} changed value — ScenarioDriver's tick math assumes {expected}");
    }
}
