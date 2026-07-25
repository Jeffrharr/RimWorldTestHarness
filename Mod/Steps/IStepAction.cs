using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

namespace RimWorldTestHarness.Mod.Steps;

// The IMPURE half of a step type: what it actually does to the live game. Paired by Type with an
// IStepSpec over in Shared, which carries the parts that must stay game-free (validation, residue,
// live-callability) so the loader and the suite planner remain testable offline.
//
// Implement this plus IStepSpec and your step works. Nothing of ours needs editing: both registries
// discover implementations by reflection over loaded mod assemblies at startup.
public interface IStepAction
{
    // Must match the Type of a registered IStepSpec. StepActionRegistry.VerifyAgainstSpecs() checks
    // that at startup and complains loudly about either half being missing, because a step that
    // validates but cannot execute (or vice versa) is a confusing half-failure to debug from a
    // report alone.
    string Type { get; }

    StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx);
}

// The executing half of the step registry. Kept separate from Shared's StepRegistry only because
// this side needs Verse types; the two are keyed identically and checked against each other.
public static class StepActionRegistry
{
    private static readonly Dictionary<string, IStepAction> Actions = new Dictionary<string, IStepAction>(StringComparer.Ordinal);

    public static bool TryGet(string type, out IStepAction? action) => Actions.TryGetValue(type, out action);

    public static IEnumerable<string> KnownTypes => Actions.Keys;

    // Same duplicate policy as StepRegistry: first registration wins and the collision is reported,
    // rather than last-writer-wins leaving the author no way to tell which one ran.
    public static void Register(IStepAction action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        if (Actions.TryGetValue(action.Type, out IStepAction? existing) && existing != null)
        {
            if (existing.GetType() != action.GetType())
            {
                Log.Error($"[RimWorldTestHarness] step type '{action.Type}' is implemented by both " +
                          $"{existing.GetType().FullName} and {action.GetType().FullName} — keeping the first.");
            }
            return;
        }

        Actions[action.Type] = action;
    }

    public static void DiscoverIn(Assembly assembly)
    {
        foreach (Type type in SafeGetTypes(assembly))
        {
            bool constructible = typeof(IStepAction).IsAssignableFrom(type) &&
                                 !type.IsAbstract && !type.IsInterface &&
                                 type.GetConstructor(Type.EmptyTypes) != null;
            if (constructible)
            {
                TryRegister(type);
            }
        }
    }

    private static void TryRegister(Type type)
    {
        try
        {
            Register((IStepAction)Activator.CreateInstance(type)!);
        }
        catch (Exception ex)
        {
            Log.Error($"[RimWorldTestHarness] could not construct step action {type.FullName}: {ex.Message}");
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Partial results beat none: one mod assembly referencing something unavailable must not
            // take out step discovery for every other mod.
            return ex.Types.Where(t => t != null)!;
        }
        catch
        {
            return Enumerable.Empty<Type>();
        }
    }
}
