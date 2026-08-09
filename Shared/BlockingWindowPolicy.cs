namespace RimWorldTestHarness.Shared;

// What the harness should do about a Window sitting on the stack while a scenario is running.
//
// Pure decision logic — no Verse types — so it compiles into both the Mod (net472, inside RimWorld)
// and the offline test project, and the classification can be tested without booting the game. The
// live half (walking Find.WindowStack, applying a name, removing the window) is Mod/DialogGuard.cs.
//
// WHY THIS EXISTS. A modal dialog with `forcePause = true` stops the tick clock. A scenario that then
// runs FastForward waits for a TicksGame target that can never arrive, and the run dies on the
// runner's 900s timeout with no error in the log — the last thing printed is whatever step ran before
// the dialog appeared, which reads as "that step hung". Two real cases, both diagnosed the slow way:
//
//   - Dialog_NamePlayerFactionOnly / Dialog_NamePlayerColony. RimWorld asks the player to name the
//     colony once it becomes a settlement. Dialog_GiveName sets forcePause = true AND
//     closeOnCancel = false, so it pauses the game and cannot be dismissed with Escape.
//   - Faction-selection prompts added by Vanilla Expanded mods on first load of a save that predates
//     them.
//
// Neither is a bug in the mod under test, and neither is something a scenario author can foresee.
public static class BlockingWindowPolicy
{
    // What to do with one window.
    public enum Decision
    {
        // Leave it alone: it is not stopping the run. Most windows are this — a main tab, an
        // inspector, anything that neither pauses nor eats input.
        Leave,

        // Resolve it properly by supplying a value and accepting, rather than closing it. Naming
        // dialogs need this: force-closing one leaves whatever asked for the name still unnamed, so
        // the game is free to ask again on a later tick and the run stalls a second time.
        AcceptWithGeneratedName,

        // Close it outright. For everything else that blocks, there is nothing to supply — the
        // dialog wants a click, and any click will do.
        ForceClose,
    }

    // The name the harness supplies when the dialog offers none. Fixed rather than randomised because
    // a scenario's screenshots are compared pixel-for-pixel between runs, and a colony name is drawn
    // on screen — a random one would put a different string in the frame each run and light up a
    // CIELAB diff for a reason that has nothing to do with the change under test.
    public const string FallbackName = "HarnessColony";

    // Which name to accept: whatever the dialog already has in the box, if anything.
    //
    // RimWorld pre-fills these — the observed prompt arrived with "Documented Coalition of Witanni"
    // and "Elegance Township" already typed in — and taking what is offered is better than
    // overwriting on two counts. It goes through the same validation the player's own click would
    // (IsValidName can reject a duplicate, and the two-field variant asks for a faction name AND a
    // settlement name, which must not collide — writing one constant into both is exactly how you
    // trip that). And it leaves the save reading like a normal game rather than one the harness
    // scribbled on.
    //
    // The fallback only applies when the box is genuinely empty, which is the case a mod-added
    // dialog can produce.
    public static string ResolveName(string existing)
    {
        string trimmed = existing?.Trim();
        return string.IsNullOrEmpty(trimmed) ? FallbackName : trimmed;
    }

    // Does this window stop a scenario from progressing?
    //
    // forcePause is the one that actually strands a run (it stops TicksGame, so FastForward waits
    // forever), but absorbInputAroundWindow is included because a modal that swallows input also
    // prevents anything the driver does from reaching the game, and because a dialog that does the
    // second usually does the first. Erring toward dismissing is right for a test harness: the cost of
    // closing a window nobody needed is nil, and the cost of leaving one up is a 15-minute timeout.
    public static bool IsBlocking(bool forcePause, bool absorbInputAroundWindow) =>
        forcePause || absorbInputAroundWindow;

    // Whether a blocking window is one the harness itself put up and must not close. Matched on the
    // type's namespace rather than a list of names so a new harness window is covered the day it is
    // written — the alternative, an allowlist of individual type names, is exactly the kind of thing
    // that silently stops covering a case after a refactor.
    public static bool IsHarnessOwned(string fullTypeName) =>
        fullTypeName != null && fullTypeName.StartsWith("RimWorldTestHarness.", System.StringComparison.Ordinal);

    // The whole decision for one window. `isGiveNameDialog` is resolved by the adapter with a real
    // type check (`window is Dialog_GiveName`) rather than by matching a name here, because the
    // interesting types are SUBCLASSES — Dialog_NamePlayerColony, Dialog_NamePlayerFactionOnly and
    // whatever a mod adds — and a string match on the base name would miss every one of them.
    public static Decision Decide(
        string fullTypeName, bool forcePause, bool absorbInputAroundWindow, bool isGiveNameDialog)
    {
        if (!IsBlocking(forcePause, absorbInputAroundWindow))
            return Decision.Leave;

        if (IsHarnessOwned(fullTypeName))
            return Decision.Leave;

        return isGiveNameDialog ? Decision.AcceptWithGeneratedName : Decision.ForceClose;
    }
}
