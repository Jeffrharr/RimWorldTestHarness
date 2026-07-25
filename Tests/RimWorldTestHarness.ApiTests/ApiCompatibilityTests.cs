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
