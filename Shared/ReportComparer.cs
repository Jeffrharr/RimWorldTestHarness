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
            Comparison = ProbeComparison.Within,
        };
    }

    // The comparison a check's recorded Comparison asks for. An unrecognised value falls to the
    // two-sided form, which is the reading a report written before ProbeComparison existed deserves —
    // and the conservative direction, since a within-check is the strictest of the three.
    public static bool Satisfies(double actual, double expected, double tolerance, string? comparison)
    {
        if (comparison == ProbeComparison.AtMost)
            return actual <= expected;

        if (comparison == ProbeComparison.AtLeast)
            return actual >= expected;

        return Math.Abs(actual - expected) <= Math.Abs(tolerance);
    }

    // The generalised CheckProbe, used by ProfileAssert. Kept beside it rather than replacing it so the
    // Probe step's call site still reads as the two-sided check it has always been.
    public static ProbeCheckResult Check(
        string name, double actual, double expected, double tolerance, string comparison)
    {
        return new ProbeCheckResult
        {
            ProbeName = name,
            ActualValue = (float)actual,
            ExpectedValue = (float)expected,
            Tolerance = (float)tolerance,
            Pass = Satisfies(actual, expected, tolerance, comparison),
            Comparison = comparison,
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

    // The same gate plus the vision tier. Only a CONFIDENT FAIL from a judge blocks — an unjudged
    // rubric leaves the run provisionally green rather than failing it, because a soft gate that
    // red-builds on "nobody has looked yet" would be turned off within a week.
    //
    // That leniency is only safe because the pending count is reported out loud (VisionGate.Describe,
    // printed by run_test.sh). A silent provisional pass would be precisely the green-run-means-less
    // failure the two-arg overload above exists to prevent.
    public static bool AllPass(
        IReadOnlyList<ProbeCheckResult> checks,
        IReadOnlyList<string> errors,
        IReadOnlyList<VisionAssert> visionAsserts) =>
        AllPass(checks, errors) && !VisionGate.AnyBlocks(visionAsserts);

    // The same argument one level up, for a multi-scenario run. Three conditions, each guarding a
    // distinct way a suite could look green while having verified less than it was asked to:
    //
    //   * no suite-level errors — an unparsable suite list, a screenshot name collision or a reload
    //     that never completed each invalidate the run as a whole, not one scenario;
    //   * at least one scenario — All() over an empty list is true, so an empty suite would otherwise
    //     pass, exactly the vacuous-truth bug the two-arg overload above exists for;
    //   * every scenario passed — including ones that never ran, which carry SuiteReportBuilder's
    //     NotRunReason in their own Errors and so fail through the per-scenario gate.
    public static bool AllPass(SuiteReport suite) =>
        suite.Errors.Count == 0 && suite.Scenarios.Count > 0 && suite.Scenarios.All(s => s.Pass);
}
