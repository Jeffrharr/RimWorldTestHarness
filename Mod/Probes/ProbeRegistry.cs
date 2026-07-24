using System.Collections.Generic;

namespace RimWorldTestHarness.Mod.Probes;

// TODO: scaffold only — registration is explicit/manual for now. A real implementation should
// probably discover IProbe implementers via reflection over loaded assemblies at startup, the
// same way Harmony patches are auto-discovered, so target mods don't need to call Register()
// from their own bootstrap.
public static class ProbeRegistry
{
    private static readonly Dictionary<string, IProbe> Probes = new();

    public static void Register(IProbe probe) => Probes[probe.Name] = probe;

    public static bool TryGet(string name, out IProbe? probe) => Probes.TryGetValue(name, out probe);
}
