using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RimWorldTestHarness.Shared.Steps;

// Every step type known to this process, keyed by its scenario-JSON `type` string.
//
// Populated by reflection rather than a hand-maintained list, for the reason the whole extension
// point exists: a central list is a file every contributor's PR has to edit, and one they can forget
// to. Built-in steps are discovered from this assembly by the static constructor; the Mod side
// additionally scans loaded mod assemblies at startup (see Mod/Steps/StepDiscovery.cs) so a
// third-party step registers by existing, not by being added to anything of ours.
//
// Discovery over an explicit Register() call is also what makes this usable OFFLINE: unit tests and
// any future spec-linting mode get the built-in vocabulary with no game and no bootstrap, which is
// what keeps StepValidator/ScenarioResidueAnalyzer/SuitePlanner testable without booting RimWorld.
public static class StepRegistry
{
    private static readonly Dictionary<string, IStepSpec> Specs = new Dictionary<string, IStepSpec>(StringComparer.Ordinal);

    // Problems found while discovering — a duplicate type, or a spec that could not be constructed.
    // Surfaced rather than thrown: a broken third-party step must not stop the harness from loading
    // and reporting the problem, but it must not be silently absent either.
    private static readonly List<string> DiscoveryErrorList = new List<string>();

    static StepRegistry()
    {
        // Built-ins live in this assembly, so a Shared-only consumer (tests, offline validation) is
        // fully equipped the moment it touches the registry.
        DiscoverIn(typeof(StepRegistry).Assembly);
    }

    public static IReadOnlyList<string> DiscoveryErrors => DiscoveryErrorList;

    // Ordered so error messages and any generated documentation list types deterministically —
    // reflection order is not guaranteed stable, and a test asserting on a message should not depend
    // on it.
    public static IReadOnlyList<string> KnownTypes =>
        Specs.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();

    public static IEnumerable<IStepSpec> All => Specs.Values;

    public static bool TryGet(string type, out IStepSpec? spec) => Specs.TryGetValue(type, out spec);

    public static bool IsKnown(string type) => Specs.ContainsKey(type);

    // Step types the interactive companion channel is allowed to run. Derived from the specs rather
    // than kept as its own set, so a new step cannot accidentally become live-callable by being
    // added to one list and not the other — the default is not-callable, and a step has to say
    // otherwise in its own definition.
    public static IEnumerable<string> LiveCallableTypes =>
        Specs.Values.Where(s => s.LiveCallable).Select(s => s.Type);

    // A duplicate type is an error, not a last-writer-wins overwrite: two steps answering to the
    // same name means scenario JSON silently gets whichever registered last, and the author has no
    // way to tell which ran.
    public static void Register(IStepSpec spec)
    {
        if (spec == null)
            throw new ArgumentNullException(nameof(spec));

        if (string.IsNullOrWhiteSpace(spec.Type))
        {
            DiscoveryErrorList.Add($"{spec.GetType().FullName} declares an empty step Type — ignored.");
            return;
        }

        if (Specs.TryGetValue(spec.Type, out IStepSpec? existing) && existing != null)
        {
            if (existing.GetType() == spec.GetType())
                return; // same spec discovered twice (e.g. an assembly scanned again) — harmless

            DiscoveryErrorList.Add(
                $"step type '{spec.Type}' is declared by both {existing.GetType().FullName} and " +
                $"{spec.GetType().FullName} — keeping the first, ignoring the second.");
            return;
        }

        Specs[spec.Type] = spec;
    }

    // Instantiates and registers every IStepSpec in the assembly that has a public parameterless
    // constructor. Specs are required to be stateless (they hold no per-run state — the drivers own
    // that), so one shared instance per type is correct.
    public static void DiscoverIn(Assembly assembly)
    {
        foreach (Type type in SafeGetTypes(assembly))
        {
            if (!IsConstructibleSpec(type))
                continue;

            try
            {
                Register((IStepSpec)Activator.CreateInstance(type)!);
            }
            catch (Exception ex)
            {
                DiscoveryErrorList.Add($"could not construct step spec {type.FullName}: {ex.Message}");
            }
        }
    }

    private static bool IsConstructibleSpec(Type type) =>
        typeof(IStepSpec).IsAssignableFrom(type) &&
        !type.IsAbstract &&
        !type.IsInterface &&
        type.GetConstructor(Type.EmptyTypes) != null;

    // A mod assembly referencing something unavailable throws here and would otherwise take out
    // discovery for every OTHER assembly too. Partial results plus a recorded reason beats none.
    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            DiscoveryErrorList.Add(
                $"partial type load from {assembly.GetName().Name}: {ex.Message}");
            return ex.Types.Where(t => t != null)!;
        }
        catch (Exception ex)
        {
            DiscoveryErrorList.Add($"could not read types from {assembly.GetName().Name}: {ex.Message}");
            return Enumerable.Empty<Type>();
        }
    }
}
