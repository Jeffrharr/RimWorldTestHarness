using Verse;

namespace RimWorldTestHarness.Mod.Probes;

// Extension point: a target mod (or this harness itself) implements one IProbe per numeric
// quantity a scenario should be able to assert on — e.g. "shadow lean at map center" for
// CelestialLighting. Keeping this as an interface rather than a hardcoded switch means adding a
// new probe never requires editing the harness itself.
public interface IProbe
{
    string Name { get; }

    float Read(Map map);
}

// Optional companion a probe MAY also implement so the live catalog can describe it to a human/Claude
// (what the number means, what unit it's in). Kept a separate interface rather than adding members to
// IProbe because Mono/net481 can't execute C# default-interface-method bodies at runtime, so widening
// IProbe would force every existing probe (e.g. CelestialLighting's ShadowLeanProbe) to add members.
// The catalog builder does a plain `probe is IProbeMetadata` check.
public interface IProbeMetadata
{
    string? Description { get; }
    string? Unit { get; }
}
