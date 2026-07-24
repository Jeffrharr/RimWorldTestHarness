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
