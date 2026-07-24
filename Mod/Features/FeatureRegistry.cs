using System;
using System.Collections.Generic;

namespace RimWorldTestHarness.Mod.Features;

// Parallel to ProbeRegistry, and for the same reason: the harness must never take a compile-time
// reference to any mod under test. A target mod registers a named setter that flips one of its own
// runtime feature flags, and the SetFeature scenario step calls it by name. The dependency only
// goes one direction — the mod's dev-only bridge assembly (e.g. CelestialLighting.Probes) calls
// Register() from its startup, the harness knows nothing about which flags exist.
//
// This is what lets one scenario screenshot an effect off, flip it on, and screenshot again for an
// A/B visual diff in a single game boot, instead of eyeballing one frame or booting the game twice.
//
// TODO (mirrors ProbeRegistry): registration is explicit/manual for now; a real implementation
// could discover a registration interface via reflection over loaded assemblies at startup so
// target mods don't need an explicit Register() call.
public static class FeatureRegistry
{
    private static readonly Dictionary<string, Action<bool>> Setters = new();

    public static void Register(string name, Action<bool> setter) => Setters[name] = setter;

    // Enumerated by the live channel's catalog builder so a client can discover which feature flags
    // the currently-loaded modset exposes for SetFeature.
    public static IEnumerable<string> Names => Setters.Keys;

    // Returns false (rather than throwing) when no feature is registered under the name, so the
    // step executor can turn it into a scenario error entry instead of crashing the frame.
    public static bool TrySet(string name, bool enabled)
    {
        if (!Setters.TryGetValue(name, out Action<bool>? setter))
            return false;
        setter(enabled);
        return true;
    }
}
