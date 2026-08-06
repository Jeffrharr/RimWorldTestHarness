using System.Collections.Generic;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Mod.Profiling;

// The tables harvested so far in the CURRENT scenario, so a ProfileAssert step can read a number out
// of one a ProfileStop step produced several steps earlier.
//
// A small piece of driver-adjacent state rather than a lookup through the report, because a step
// action deliberately cannot see the report: StepContext carries the live map and a screenshot path
// resolver and nothing else, so that batch and live runs cannot drift on what a step is allowed to
// touch. Handing the report in for one step type would have made every future step able to rewrite
// any part of it.
//
// Scoped per scenario and cleared by ScenarioDriver at each scenario boundary. Not doing so would let
// a ProfileAssert in scenario B silently read scenario A's table under the same name and pass — the
// same cross-contamination the residue rules exist to prevent, but inside the harness's own
// bookkeeping where no isolation policy would have caught it.
internal static class ProfileResults
{
    private static readonly Dictionary<string, ProfileTable> Tables = new Dictionary<string, ProfileTable>();

    public static void Clear() => Tables.Clear();

    // A duplicate name overwrites, and the caller (ProfileStopAction) refuses it first. Two tables
    // under one name would make a ProfileAssert's target depend on step order, which is exactly the
    // kind of thing nobody re-derives when a scenario starts failing a year later.
    public static bool Contains(string name) => Tables.ContainsKey(name);

    public static void Store(ProfileTable table) => Tables[table.Name] = table;

    public static bool TryGet(string name, out ProfileTable? table) => Tables.TryGetValue(name, out table);

    public static IEnumerable<string> Names => Tables.Keys;
}
