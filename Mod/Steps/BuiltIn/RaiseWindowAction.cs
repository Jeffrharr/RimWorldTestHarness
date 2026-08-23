using System;
using System.Collections.Generic;
using RimWorldTestHarness.Shared.Steps.BuiltIn;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching half of RaiseWindow. See Shared/Steps/BuiltIn/RaiseWindowStep.cs for the pure
// half and for why a step that raises a mod's own window has to exist.
//
// EVERY FAILURE IS NAMED, and that is most of this file. A step that could not find the type, or
// found something that is not a Window, or found a Window with no parameterless constructor, all
// have the same symptom if reported vaguely: a screenshot with no window in it, which is
// indistinguishable from a window that WAS raised and then refused by the dialog guard. The three
// are fixed in completely different places — a typo, a wrong type, a missing constructor, a missing
// --keep-dialogs — so the message has to say which.
public sealed class RaiseWindowAction : IStepAction
{
    public string Type => RaiseWindowStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!RaiseWindowStep.TryPlan(args, out string typeName, out string? error))
            return StepOutcome.Fail($"RaiseWindow: {error}");

        Type type = GenTypes.GetTypeInAnyAssembly(typeName);
        if (type == null)
            return StepOutcome.Fail(
                $"RaiseWindow: no type '{typeName}' in any loaded assembly. Give the full name "
                + "including namespace, and check the mod that owns it is in this run's mod list.");

        if (!typeof(Window).IsAssignableFrom(type))
            return StepOutcome.Fail(
                $"RaiseWindow: '{typeName}' is not a Verse.Window, so it cannot be put on the stack.");

        if (type.GetConstructor(System.Type.EmptyTypes) == null)
            return StepOutcome.Fail(
                $"RaiseWindow: '{typeName}' has no public parameterless constructor. Add one that "
                + "reads whatever live state the window would normally be handed — this step has no "
                + "way to build constructor arguments for it.");

        Window window;
        try
        {
            window = (Window)Activator.CreateInstance(type);
        }
        catch (Exception e)
        {
            // Unwrapped: Activator wraps whatever the constructor threw in a
            // TargetInvocationException, whose own message says nothing about the actual fault.
            Exception cause = e is System.Reflection.TargetInvocationException invocation
                ? invocation.InnerException ?? e
                : e;

            return StepOutcome.Fail(
                $"RaiseWindow: constructing '{typeName}' threw {cause.GetType().Name}: {cause.Message}");
        }

        Find.WindowStack.Add(window);

        // One frame, for the same reason RaiseTestDialog waits one: if the guard is going to refuse
        // this window it does so at Add, so waiting a frame means the report's DismissedDialogs /
        // KeptDialogs are populated before a following Screenshot or assertion reads them.
        return new StepOutcome { WaitFrames = 1 };
    }
}
