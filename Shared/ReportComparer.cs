using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldTestHarness.Shared;

// Pure — no game/file/JSON dependency. This is the numeric half of "spec-driven design": given
// what a probe actually read and what the scenario spec said to expect, decide pass/fail. Kept
// separate from ScenarioReport (the data) and from the Mod-side probe execution (the impure part
// that reads live Map state) so it can be edge-case tested offline, same pattern as
// CelestialLighting/Source/Formulas.cs.
public static class ReportComparer
{
    public static bool WithinTolerance(float actual, float expected, float tolerance) =>
        Math.Abs(actual - expected) <= Math.Abs(tolerance);

    public static ProbeCheckResult CheckProbe(string probeName, float actual, float expected, float tolerance)
    {
        return new ProbeCheckResult
        {
            ProbeName = probeName,
            ActualValue = actual,
            ExpectedValue = expected,
            Tolerance = tolerance,
            Pass = WithinTolerance(actual, expected, tolerance),
        };
    }

    // A scenario passes only if every probe check in it passed. Screenshot steps never affect
    // Pass — they're the complementary visual-confirm channel, not part of the numeric gate.
    public static bool AllPass(IReadOnlyList<ProbeCheckResult> checks) => checks.All(c => c.Pass);

    // The gate a run should actually use. Errors have to count, because AllPass over an empty list is
    // true: without this, a scenario whose every step errored and which has no Probe step reports
    // Pass and the runner exits 0, with the errors printed but ungated. That matters most for steps
    // that only produce images — a failed PlaceThings would otherwise leave a plausible-looking empty
    // screenshot on a green run, which is the silent-underverification failure this harness exists to
    // prevent.
    public static bool AllPass(IReadOnlyList<ProbeCheckResult> checks, IReadOnlyList<string> errors) =>
        errors.Count == 0 && AllPass(checks);
}
