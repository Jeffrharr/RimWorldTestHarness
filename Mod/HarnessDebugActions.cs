using System.IO;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldTestHarness.Mod;

// Screenshot as a first-class RimWorld dev command. Registering it as a [LudeonTK.DebugAction] means
// it appears in the game's OWN dev-action menu (under a "RimWorldTestHarness" category), and the
// live companion channel drives the exact same capture core — so we extend the game's dev tooling
// rather than reinventing a parallel one. This is the pattern for any future harness action: make it
// a real [DebugAction] with a shared core, don't bolt on a bespoke path.
public static class HarnessDebugActions
{
    // Shared capture core, used by the [DebugAction] below AND by the live channel's Screenshot
    // action (StepExecutor). Unity's ScreenCapture writes asynchronously over the next few frames;
    // a caller that needs the finished file (the live channel) waits for it to appear on disk.
    public static void CaptureScreenshotTo(string absolutePath)
    {
        string? dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        ScreenCapture.CaptureScreenshot(absolutePath);
    }

    private static int _menuShotCounter;

    // Shows up in RimWorld's dev-action menu. Saves into the normal Screenshots folder with a
    // harness-tagged, collision-free name. The live channel doesn't go through here — it calls
    // CaptureScreenshotTo directly with its own session path so it can return the PNG.
    [DebugAction("RimWorldTestHarness", "Screenshot (harness)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void DevActionScreenshot()
    {
        _menuShotCounter++;
        string name = $"rwth_{Find.TickManager.TicksGame}_{_menuShotCounter}.png";
        string path = Path.Combine(GenFilePaths.ScreenshotFolderPath, name);
        CaptureScreenshotTo(path);
        Messages.Message($"RWTH screenshot -> {path}", MessageTypeDefOf.TaskCompletion, historical: false);
    }
}
