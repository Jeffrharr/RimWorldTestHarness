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

    // What ResetAll puts each flag back to. Tracked separately from Setters because a setter is
    // write-only (Action<bool>) — the harness can flip a mod's flag but cannot read it back, so the
    // only way to restore one is to have been told its resting value at registration.
    private static readonly Dictionary<string, bool> Defaults = new();

    // The two-arg overload is kept as a real overload rather than becoming an optional parameter on
    // the three-arg one: optional parameters are baked at the CALL SITE, so already-shipped bridge
    // assemblies (e.g. CelestialLighting.Probes) compiled against the old two-parameter signature
    // would fail to bind at runtime with a MissingMethodException.
    //
    // Defaulting to true assumes a feature flag's resting state is "the shipped mod's behaviour is on",
    // which is what these flags are for — SetFeature enabled=false is the A/B "off" case. A mod whose
    // flag defaults off should say so with the overload below.
    public static void Register(string name, Action<bool> setter) => Register(name, setter, defaultEnabled: true);

    public static void Register(string name, Action<bool> setter, bool defaultEnabled)
    {
        Setters[name] = setter;
        Defaults[name] = defaultEnabled;
    }

    // Enumerated by the live channel's catalog builder so a client can discover which feature flags
    // the currently-loaded modset exposes for SetFeature.
    public static IEnumerable<string> Names => Setters.Keys;

    // Puts every registered flag back to its registered default. Called between scenarios in a suite
    // (WorldStateReset.Restore), never by the live companion channel — that one points at a real
    // player's running game, where silently rewriting mod settings would be a nasty surprise.
    //
    // Resets ALL flags rather than only the ones a scenario touched: the set of flags is small, setting
    // one to the value it already has is free, and tracking "touched" would mean trusting the driver to
    // have noticed every SetFeature — including ones that ran in an earlier scenario of the same suite.
    public static void ResetAll()
    {
        foreach (KeyValuePair<string, bool> entry in Defaults)
            Setters[entry.Key](entry.Value);
    }

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
