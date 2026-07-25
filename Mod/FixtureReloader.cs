using System;
using System.IO;
using LudeonTK;
using RimWorld;
using Verse;

namespace RimWorldTestHarness.Mod;

// Mid-session reload of the save the run booted from — the "quickload between scenarios" that makes
// running several scenarios in one game load actually isolated rather than merely sequential.
//
// Why this rather than resetting what we changed: PlaceThings spawns with WipeMode.Vanish (destroying
// whatever occupied the footprint), SetTerrain repaints ground, and both lift fog of war across the
// whole map. None of that can be put back by assigning values, so a scenario following one that did
// scene setup would run against a world it never asked for. A reload costs seconds; a boot costs
// minutes. See DESIGN.md, "Batching scenarios into one load".
//
// Vanilla does exactly this for its own in-game Load Game: GameDataSaveLoader.LoadGame(name) queues a
// long event that clears the maps/world, installs a fresh Game with InitData.gameToLoad set, and
// reloads the "Play" scene, where Root_Play.Start() picks up gameToLoad and runs
// SavedGameLoaderNow.LoadGameFromSaveFileNow. Two consequences worth knowing:
//
//   * Root.checkedAutostartSaveFile is already true by then, so the reloaded Play scene does NOT
//     re-trigger vanilla's autostart-save path — the gameToLoad branch is what runs, which is why we
//     pass the save name explicitly rather than relying on autostart a second time.
//   * Our Harmony patch is on Root_Play.Update as a method, and this assembly's statics outlive the
//     scene, so the driver keeps being pumped across the scene reload with its state intact.
//
// This is deliberately a real [DebugAction] as well as a driver entry point, following the same rule
// as HarnessDebugActions' screenshot: harness capabilities are game dev commands sharing one core, not
// a parallel private path. It also means a human can verify a mid-session reload works with one click
// in a normally-launched game, without running a batch.
public static class FixtureReloader
{
    // Runner/run_test.sh copies the scenario's fixture to Saves/autostart.rws for vanilla's autostart
    // mechanism, so that file is also the natural thing to reload mid-run. Named here (not just passed
    // in) so the dev action below has something to offer without arguments.
    public const string AutostartSaveName = "autostart";

    // Returns null on success, or a reason string. Failure is returned rather than thrown because the
    // caller is inside a Root_Play.Update postfix, and a throwing frame is a worse outcome than a
    // suite error.
    //
    // The existence pre-check matters: LoadGame only QUEUES the load, so a missing file surfaces as an
    // exception thrown on the long-event thread minutes later, by which point the driver is sitting in
    // WaitingForMap and all the caller sees is a timeout.
    public static string? Reload(string saveName)
    {
        string path = GenFilePaths.FilePathForSavedGame(saveName);
        if (!File.Exists(path))
            return $"no save file at '{path}' to reload (save name '{saveName}')";

        try
        {
            GameDataSaveLoader.LoadGame(saveName);
            return null;
        }
        catch (Exception ex)
        {
            return $"reload of save '{saveName}' threw: {ex.Message}";
        }
    }

    [DebugAction("RimWorldTestHarness", "Quickload autostart save",
        allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void DevActionReloadAutostart()
    {
        string? error = Reload(AutostartSaveName);
        // Nothing is reported on success: the screen is about to be replaced by the loading screen,
        // which is the confirmation.
        if (error != null)
            Messages.Message($"RWTH quickload failed: {error}", MessageTypeDefOf.RejectInput, historical: false);
    }
}
