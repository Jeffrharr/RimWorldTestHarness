using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorldTestHarness.Shared.Steps.BuiltIn;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of RaiseTestDialog. See Shared/Steps/BuiltIn/RaiseTestDialogStep.cs for the
// pure half and for why a step that deliberately raises a blocking modal has to exist at all.
//
// The whole step is one Find.WindowStack.Add call. That is the point: Add is exactly the method
// Patch_SuppressBlockingWindows prefixes, so this exercises the guard through the same seam the game
// itself uses, rather than through a back door that could pass while the real path stays broken.
public sealed class RaiseTestDialogAction : IStepAction
{
    public string Type => RaiseTestDialogStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!RaiseTestDialogStep.TryPlan(args, out string kind, out string? error))
            return StepOutcome.Fail($"RaiseTestDialog: {error}");

        Window window = kind == RaiseTestDialogStep.KindMessageBox
            ? BuildMessageBox()
            : BuildNamingDialog(ctx, out error);

        if (window == null)
            return StepOutcome.Fail($"RaiseTestDialog: {error}");

        Find.WindowStack.Add(window);

        // One frame, so a guard that suppresses at Add has already run by the next step and the
        // report's DismissedDialogs is populated before anything asserts on it.
        return new StepOutcome { WaitFrames = 1 };
    }

    // Dialog_MessageBox sets absorbInputAroundWindow, which is what makes it blocking; forcePause is
    // requested explicitly so this stands in for the genuinely run-stranding case rather than merely
    // the annoying one.
    private static Window BuildMessageBox() =>
        new Dialog_MessageBox("RimWorldTestHarness: deliberate blocking dialog") { forcePause = true };

    // The real thing: the two-field prompt observed stranding a run. It needs the player's settlement,
    // which is the map's own parent on any ordinary colony map.
    private static Window BuildNamingDialog(StepContext ctx, out string? error)
    {
        error = null;

        Settlement settlement = ctx.Map?.Parent as Settlement
            ?? Find.WorldObjects?.Settlements?.FirstOrDefault(s => s.Faction == Faction.OfPlayer);

        if (settlement == null)
        {
            // Named rather than silently downgraded to a message box: a scenario that asked to test
            // the naming path and quietly got the other one would report a pass over the wrong thing.
            error = "no player settlement on this map to name — "
                + $"use kind '{RaiseTestDialogStep.KindMessageBox}' on a map without one";
            return null;
        }

        return new Dialog_NamePlayerFactionAndSettlement(settlement);
    }
}
