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
