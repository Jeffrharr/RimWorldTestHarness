namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// RaiseWindow — construct a Window by type name and put it on the stack, so a mod's own UI can be
// photographed by the Screenshot step that follows.
//
// WHY THIS EXISTS, given RaiseTestDialog already raises windows. That one raises VANILLA dialogs and
// exists to test the harness's own guard; this one raises YOURS. Before it there was no way to get a
// mod's window in front of the camera at all: a window a mod raises during play appears at a moment
// no scenario can arrange, and one it raises at the main menu is discarded before the first step
// runs (each UIRoot owns its own WindowStack). So a mod's UI was the one visible thing this harness
// could not photograph, and the only review it could get was somebody reading the layout arithmetic.
//
// PAIR IT WITH --keep-dialogs. Most windows worth photographing are modal, and a modal is exactly
// what DialogGuard refuses at Add — so without that flag this step raises a window that is gone
// before the next frame, and the screenshot shows a colony. That is not a bug in either half, but it
// is the mistake everybody makes once, so the runner names the window it dropped.
//
// The type is named in full and constructed with its parameterless constructor. That is a real
// constraint on the mod side rather than an oversight: a window needing constructor arguments would
// need this step to build them, which means either a serialisation format for arbitrary objects or a
// registry of factories, and both are a lot of machinery to avoid writing one extra constructor. A
// mod that wants its window photographed adds a parameterless ctor reading whatever live state it
// would normally be handed — which is usually a sensible thing for it to have anyway.
public sealed class RaiseWindowStep : IStepSpec
{
    public const string StepType = "RaiseWindow";

    // Full type name, namespace included: "CelestialLighting.Dialog_UpdateNotice". Resolved against
    // every loaded assembly at execution time, because a name that resolves depends on the mod list
    // and this half has no way to look at it.
    public const string TypeArg = "type";

    public string Type => StepType;

    // See ScenarioResidue.Windows. Under --keep-dialogs the window really is still up when the next
    // scenario starts, and a modal covering the middle of its screenshots reads as a rendering bug
    // rather than as leftover state.
    public ScenarioResidue Residue => ScenarioResidue.Windows;

    // Never exposed to the interactive companion channel, for the same reason RaiseTestDialog is
    // not: it jams a window in front of whatever a real player is doing, which is precisely what
    // that channel promises not to do to a live colony.
    public bool LiveCallable => false;

    public bool TryValidate(
        System.Collections.Generic.IReadOnlyDictionary<string, string> args, out string? error) =>
        TryPlan(args, out _, out error);

    // Only checks that a name was given. Whether it names a real, constructible Window is a question
    // about the loaded mod list, which this half deliberately cannot see — the action answers it,
    // and fails the step by name rather than silently raising nothing.
    public static bool TryPlan(
        System.Collections.Generic.IReadOnlyDictionary<string, string> args,
        out string typeName, out string? error)
    {
        typeName = string.Empty;
        error = null;

        if (!args.TryGetValue(TypeArg, out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            error = $"missing required arg '{TypeArg}' (the window's full type name, "
                + "e.g. 'CelestialLighting.Dialog_UpdateNotice')";
            return false;
        }

        typeName = raw.Trim();
        return true;
    }
}
