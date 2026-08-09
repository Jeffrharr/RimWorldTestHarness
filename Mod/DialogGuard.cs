using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod;

// Clears modal dialogs that would otherwise strand a scenario. See Shared/BlockingWindowPolicy for
// the decision logic and the full "why"; this file is the live half — it walks Find.WindowStack,
// applies the policy, and records what it did.
//
// Two entry points, and the first is the one that matters:
//
//   TrySuppress — called from Patch_SuppressBlockingWindows' prefix on WindowStack.Add, so a blocking
//     dialog is refused before it is ever stacked. forcePause applies the instant a window is added,
//     so this is the only version with no window of vulnerability: nothing pauses, and nothing can
//     appear in a Screenshot frame.
//   ClearBlockingDialogs — pumped once per frame from ScenarioDriver.Tick, BEFORE the wait states, as
//     a backstop for windows already up before the scenario went Active. That ordering matters too:
//     the states that strand a run (FastForward waiting on TicksGame, the screenshot-file wait) all
//     return early, so a sweep placed after them would never run on the frames that need it.
//
// EVERY DISMISSAL IS RECORDED, never silent. A harness that quietly clicks OK on whatever the game
// asks is a harness that can hide a real problem — a mod throwing up an error dialog every tick would
// present as a clean run. The names go into ScenarioReport.DismissedDialogs and are printed by
// run_test.sh, so "this run needed six dialogs cleared" is visible rather than inferred.
public static class DialogGuard
{
    // Dismissed window type names, in the order they were cleared, for the current scenario. Cleared
    // by the driver when a scenario begins so one scenario's dialogs are never attributed to the next.
    private static readonly List<string> Dismissed = new List<string>();

    public static IReadOnlyList<string> DismissedTypeNames => Dismissed;

    public static void Reset() => Dismissed.Clear();

    // Walks the window stack once and resolves anything blocking. Returns how many it cleared, so a
    // caller can log a single line per frame rather than one per window.
    //
    // Iterates over a COPY of the stack: resolving a window removes it (and a naming dialog's Named()
    // can add a message window of its own), and mutating the live list mid-enumeration throws.
    public static int ClearBlockingDialogs()
    {
        WindowStack stack = Find.WindowStack;
        if (stack == null)
            return 0;

        List<Window> windows = new List<Window>(stack.Windows);
        int cleared = 0;

        for (int i = 0; i < windows.Count; i++)
        {
            if (Resolve(windows[i]))
                cleared++;
        }

        return cleared;
    }

    // The Add-time path. Returns true if the caller should DROP this window entirely.
    //
    // A naming dialog is still resolved on its way out rather than merely dropped: it was raised
    // because something has no name, and dropping it silently would leave that true — the game is
    // then free to raise it again on a later tick, so the stall would come back and the fix would look
    // flaky rather than wrong. Resolving costs nothing here because
    // Dialog_NamePlayerFactionAndSettlement generates AND validates both names in its own constructor
    // (its generators take IsValidName/IsValidSecondName as validators), so by the time Add is called
    // there is already a good name sitting in each box.
    public static bool TrySuppress(Window window)
    {
        if (window == null)
            return false;

        Type type = window.GetType();
        BlockingWindowPolicy.Decision decision = BlockingWindowPolicy.Decide(
            type.FullName, window.forcePause, window.absorbInputAroundWindow,
            window is Dialog_GiveName);

        switch (decision)
        {
            case BlockingWindowPolicy.Decision.AcceptWithGeneratedName:
                ApplyName((Dialog_GiveName)window, type, "suppressed+named");
                return true;
            case BlockingWindowPolicy.Decision.ForceClose:
                Record(type, "suppressed");
                return true;
            default:
                return false;
        }
    }


    private static bool Resolve(Window window)
    {
        if (window == null)
            return false;

        Type type = window.GetType();
        BlockingWindowPolicy.Decision decision = BlockingWindowPolicy.Decide(
            type.FullName, window.forcePause, window.absorbInputAroundWindow,
            window is Dialog_GiveName);

        switch (decision)
        {
            case BlockingWindowPolicy.Decision.Leave:
                return false;
            case BlockingWindowPolicy.Decision.AcceptWithGeneratedName:
                return AcceptName((Dialog_GiveName)window, type);
            case BlockingWindowPolicy.Decision.ForceClose:
                return ForceClose(window, type);
            default:
                return false;
        }
    }

    // Resolves a naming dialog the way clicking OK does: set the field, call the protected Named hook,
    // then remove the window. Reflection because curName/Named/useSecondName are all protected, and
    // subclassing is not an option — the instance already exists and belongs to the game.
    //
    // Why not simply close it. Dialog_GiveName is raised BECAUSE something has no name; closing it
    // leaves that true, so the game may raise it again on a later tick and the run stalls twice. The
    // accept path also fires whatever Named() does (renaming the faction, the settlement), which is
    // the state a scenario running after this point would otherwise be missing.
    private static bool AcceptName(Dialog_GiveName dialog, Type type)
    {
        if (!ApplyName(dialog, type, "named"))
            return ForceClose(dialog, type);

        Find.WindowStack.TryRemove(dialog, doCloseSound: false);
        return true;
    }

    // Applies the dialog's own names through its protected Named/NamedSecond hooks. Does NOT touch the
    // window stack, so it serves both callers: the Add-time path (where the window is about to be
    // dropped and was never stacked) and the sweep (which removes it afterwards).
    private static bool ApplyName(Dialog_GiveName dialog, Type type, string how)
    {
        try
        {
            // Accept the name RimWorld already put in the box rather than imposing one — see
            // BlockingWindowPolicy.ResolveName for why that matters to validation.
            string name = BlockingWindowPolicy.ResolveName(ReadProtectedString(dialog, type, "curName"));
            SetProtectedField(dialog, type, "curName", name);
            InvokeProtected(dialog, type, "Named", name);

            // The two-field variant (name your FACTION and this settlement) is the one actually seen
            // in the wild — it is what stranded indoor_glow_lamp.json. Leaving its second half unset
            // is the same re-prompt trap this whole method exists to avoid.
            if (ReadProtectedBool(dialog, type, "useSecondName"))
            {
                string second = BlockingWindowPolicy.ResolveName(
                    ReadProtectedString(dialog, type, "curSecondName"));
                SetProtectedField(dialog, type, "curSecondName", second);
                InvokeProtected(dialog, type, "NamedSecond", second);
            }

            Record(type, how);
            return true;
        }
        catch (Exception e)
        {
            // Reported rather than swallowed: the reflection breaking is an upstream change worth
            // knowing about, not a routine condition. Callers fall back to dropping the window anyway,
            // because an unresolved naming dialog pauses the game forever and a partial fix beats none.
            Log.Warning(
                $"[RWTH] Could not accept {type.Name} by name ({e.GetType().Name}: {e.Message}); "
                + "dropping it unresolved instead. The game may re-raise it.");
            return false;
        }
    }

    private static bool ForceClose(Window window, Type type)
    {
        Find.WindowStack.TryRemove(window, doCloseSound: false);
        Record(type, "closed");
        return true;
    }

    private static void Record(Type type, string how)
    {
        string entry = $"{type.FullName} ({how})";
        Dismissed.Add(entry);
        Log.Message($"RWTH: dismissed blocking dialog {entry}");
    }

    // AccessTools rather than Type.GetField: the fields live on Dialog_GiveName while `type` is the
    // concrete subclass, and GetField does not search base types. AccessTools does.
    private static void SetProtectedField(object target, Type type, string name, object value)
    {
        FieldInfo field = AccessTools.Field(type, name);
        if (field == null)
            throw new MissingFieldException(type.FullName, name);

        field.SetValue(target, value);
    }

    private static string ReadProtectedString(object target, Type type, string name)
    {
        FieldInfo field = AccessTools.Field(type, name);
        return field?.GetValue(target) as string;
    }

    private static bool ReadProtectedBool(object target, Type type, string name)
    {
        FieldInfo field = AccessTools.Field(type, name);
        return field != null && (bool)field.GetValue(target);
    }

    private static void InvokeProtected(object target, Type type, string name, string arg)
    {
        MethodInfo method = AccessTools.Method(type, name, new[] { typeof(string) });
        if (method == null)
            throw new MissingMethodException(type.FullName, name);

        method.Invoke(target, new object[] { arg });
    }
}
