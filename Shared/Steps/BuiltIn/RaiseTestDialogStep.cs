namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// RaiseTestDialog — put a real, blocking, vanilla modal on the window stack on purpose.
//
// EXISTS ONLY TO TEST THE HARNESS ITSELF, specifically Patch_SuppressBlockingWindows and DialogGuard.
// Nothing a mod author writes should ever need it.
//
// The reason it has to exist. The bug it guards against is a colony-naming prompt appearing partway
// through a run, pausing the game and stranding the scenario until the runner's 900s timeout. That
// prompt is raised by the GAME, on its own schedule — which means "the run passed" is not evidence
// the guard works, because the prompt may simply not have been raised that time. That is not
// hypothetical: the first run after the guard landed passed with DismissedDialogs empty, proving only
// that the scenario CAN pass. Without a way to raise the dialog deliberately, the feature could only
// ever be argued for, never demonstrated.
//
// Pair it with an Assert on the report, or read the "dismissed dialog:" lines the runner prints: the
// step raises the dialog, and a working guard means the scenario continues past it AND records it.
// A broken guard means this step hangs the run, which is the correct failure for a test of a hang.
public sealed class RaiseTestDialogStep : IStepSpec
{
    public const string StepType = "RaiseTestDialog";

    // Which flavour to raise. The two map to the two branches of BlockingWindowPolicy.Decide, because
    // they are resolved very differently and a test of one says nothing about the other.
    public const string KindArg = "kind";

    // Dialog_NamePlayerFactionAndSettlement — the one actually observed stranding a run. forcePause,
    // closeOnCancel false, and TWO name fields. Resolved by accepting the names it generated for
    // itself, not by closing it.
    public const string KindName = "name";

    // Dialog_MessageBox — stands in for the other family: a faction prompt from a Vanilla Expanded
    // mod, an error box. Nothing to supply, so the guard just refuses it.
    public const string KindMessageBox = "messagebox";

    public const string DefaultKind = KindName;

    public string Type => StepType;

    // No map residue: a suppressed dialog leaves nothing behind, and an accepted naming dialog only
    // renames the player faction and settlement — world state, not map state, and not something a
    // later scenario reads. Marked None deliberately rather than by omission.
    public ScenarioResidue Residue => ScenarioResidue.None;

    // Never exposed to the interactive companion channel: it exists to jam a modal in front of
    // whatever is running, which is precisely what that channel promises not to do to a real colony.
    public bool LiveCallable => false;

    public bool TryValidate(
        System.Collections.Generic.IReadOnlyDictionary<string, string> args, out string? error) =>
        TryPlan(args, out _, out error);

    public static bool TryPlan(
        System.Collections.Generic.IReadOnlyDictionary<string, string> args,
        out string kind, out string? error)
    {
        kind = DefaultKind;
        error = null;

        if (!args.TryGetValue(KindArg, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return true;

        string normalized = raw.Trim().ToLowerInvariant();
        if (normalized != KindName && normalized != KindMessageBox)
        {
            error = $"unknown {KindArg} '{raw}' — expected '{KindName}' or '{KindMessageBox}'";
            return false;
        }

        kind = normalized;
        return true;
    }
}
