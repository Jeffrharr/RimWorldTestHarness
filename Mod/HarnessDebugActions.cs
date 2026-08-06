using System.Collections.Generic;
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
    //
    // hideUi drives vanilla's own screenshot mode, which suppresses the dev toolbar, colonist bar,
    // main buttons, alerts, letters, tutor, messages and tooltips. It defaults to OFF so the two
    // interactive callers are unaffected: the dev-menu action below behaves as it always has, and
    // the live channel points at a real player's running game, where leaving the UI hidden would be
    // a nasty surprise. Only the batch path opts in — and it never restores the flag here, because
    // the capture completes over later frames; ScenarioDriver.Finish clears it at end of run. Any
    // future caller that passes true owns restoring it.
    public static void CaptureScreenshotTo(string absolutePath, bool hideUi = false)
    {
        string? dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Safe to set in the same frame as the capture: Unity runs Update (where the driver pumps
        // steps) before OnGUI and rendering, so the frame grabbed at end-of-frame is already clean.
        if (hideUi)
        {
            SetScreenshotMode(true);
            CloseDevWindows();
        }

        ScreenCapture.CaptureScreenshot(absolutePath);
    }

    // Null-guarded because UIRoot only exists once a scene is up, and a caller that got here early
    // should not eat a NullReferenceException over a cosmetic flag.
    public static void SetScreenshotMode(bool active)
    {
        if (Find.UIRoot != null)
            Find.UIRoot.screenshotMode.Active = active;
    }

    // Closes RimWorld's dev-tool windows so they do not sit on top of a captured frame.
    //
    // WHY THIS IS NOT COVERED BY screenshotMode. Vanilla's screenshot mode suppresses the things it
    // considers "UI chrome" — dev toolbar, colonist bar, main buttons, alerts, letters, messages —
    // but it does NOT touch the window stack, so anything actually OPEN stays drawn over the shot.
    //
    // That matters here specifically because every harness run forces dev mode on (autostart is
    // gated on it), and RimWorld auto-opens EditWindow_Log on the first warning. A stock mod folder
    // produces plenty — missing <downloadUrl> on half the Workshop, duplicate packageIds, a hidden
    // ritual precept being added on load — so in practice the log window is open before the first
    // scenario step runs, and every screenshot came out with a grey panel across the middle third.
    // The A/B pair is still *diffable* (the panel is identical in both frames and cancels out), but
    // it is useless as a human-readable before/after, which is most of what screenshots are for.
    //
    // Closes the whole EditWindow family rather than EditWindow_Log alone: the inspector, def editor
    // and tweak-values windows are equally capable of covering the map, and a scenario that opened
    // one deliberately would not be asking for hideUi.
    //
    // The window list is snapshotted before closing because Close() mutates it — iterating the live
    // IList while removing from it is the standard way to miss half the entries.
    private static void CloseDevWindows()
    {
        if (Find.WindowStack == null)
            return;

        List<Window> open = new List<Window>(Find.WindowStack.Windows);

        foreach (Window window in open)
        {
            if (window is LudeonTK.EditWindow)
                window.Close(doCloseSound: false);
        }
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
