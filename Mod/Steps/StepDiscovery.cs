using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorldTestHarness.Shared.Steps;
using Verse;

namespace RimWorldTestHarness.Mod.Steps;

// Finds every step definition in every loaded mod assembly, once, at startup.
//
// Scanning loaded assemblies (rather than requiring a Register() call from each mod's bootstrap) is
// what makes a third-party step work by simply existing. It mirrors what DevActionCatalog already
// does for RimWorld's own [DebugAction] methods, and it is the answer to the standing TODOs on
// ProbeRegistry/FeatureRegistry asking for the same treatment.
public static class StepDiscovery
{
    private static bool _done;

    // Idempotent: HarnessMod's static constructor and the live channel both want the registry
    // populated, and whichever runs first should pay for it.
    public static void RunOnce()
    {
        if (_done)
            return;

        _done = true;

        foreach (Assembly assembly in ModAssemblies())
        {
            StepRegistry.DiscoverIn(assembly);
            StepActionRegistry.DiscoverIn(assembly);
        }

        ReportDiscoveryErrors();
        VerifyHalvesMatch();
    }

    // Only mod assemblies, not the whole AppDomain: scanning Unity/BCL assemblies would cost startup
    // time to find nothing, and RimWorld already tracks exactly this list.
    private static IEnumerable<Assembly> ModAssemblies() =>
        LoadedModManager.RunningMods
            .SelectMany(m => m.assemblies.loadedAssemblies)
            .Distinct();

    private static void ReportDiscoveryErrors()
    {
        foreach (string error in StepRegistry.DiscoveryErrors)
            Log.Error($"[RimWorldTestHarness] step discovery: {error}");
    }

    // The one real cost of splitting a step across two assemblies is that half of it can go missing.
    // A spec with no action validates fine and then fails at execution with "no action registered";
    // an action with no spec is rejected at load as an unknown type. Both are confusing to diagnose
    // from a report, so they are called out here at startup, in the log, with the type name.
    //
    // Composites are exempt: they desugar at load and are never executed, so having no action is
    // correct for them rather than a mistake.
    private static void VerifyHalvesMatch()
    {
        foreach (IStepSpec spec in StepRegistry.All)
        {
            bool isComposite = spec is IStepExpander;
            bool missingAction = !isComposite && !StepActionRegistry.TryGet(spec.Type, out _);
            if (missingAction)
            {
                Log.Error($"[RimWorldTestHarness] step '{spec.Type}' has a spec " +
                          $"({spec.GetType().FullName}) but no IStepAction — it will validate and then " +
                          "fail at execution.");
            }
        }

        foreach (string type in StepActionRegistry.KnownTypes)
        {
            if (!StepRegistry.IsKnown(type))
            {
                Log.Error($"[RimWorldTestHarness] step '{type}' has an IStepAction but no IStepSpec — " +
                          "scenarios using it will be rejected at load as an unknown type.");
            }
        }
    }
}
