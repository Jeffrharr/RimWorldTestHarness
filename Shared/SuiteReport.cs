using System.Collections.Generic;
using System.Text.Json;

namespace RimWorldTestHarness.Shared;

// The report a multi-scenario run writes: a wrapper around one unchanged ScenarioReport per scenario.
//
// Wrapping rather than extending ScenarioReport keeps the single-scenario shape byte-identical (see
// SuiteReportSerializer), which matters because Runner/run_test.sh, its Step 7 gate and anything else
// reading Runner/reports/*.json all know that shape today. A consumer tells the two apart by the
// presence of the "Scenarios" key.
//
// Serialized with System.Text.Json's default PascalCase naming, no policy — same as ScenarioReport,
// because run_test.sh's inline Python reads the C# property names literally.
public sealed class SuiteReport
{
    public bool Pass { get; set; }

    // One entry per scenario the suite was ASKED to run, in the order it was asked for — including
    // scenarios a mid-suite abort meant we never reached, which carry an explicit "did not run" error
    // rather than being omitted. A suite that quietly listed fewer scenarios than it was given would
    // be the truncation-looks-like-success failure this harness exists to prevent.
    public List<ScenarioReport> Scenarios { get; set; } = new();

    // Suite-level failures that belong to no single scenario: an unparsable suite list, a screenshot
    // name collision, a reload that never completed.
    public List<string> Errors { get; set; } = new();

    // The isolation plan in human-readable form, plus any shortfall the caller explicitly accepted
    // (isolation=never). Informational — these never affect Pass — but recorded in the report so a
    // green suite says out loud which scenarios shared a world.
    public List<string> IsolationNotes { get; set; } = new();
}

public static class SuiteReportBuilder
{
    public const string NotRunReason =
        "scenario did not run — the suite aborted before reaching it (see suite Errors)";

    public static ScenarioReport ForScenario(ScenarioSpec spec)
    {
        ScenarioReport report = new ScenarioReport { ScenarioName = spec.Name };
        // Anything the loader rejected while desugaring composite steps travels into the report up
        // front, so a scenario that lost a whole Timelapse sweep to a typo says so rather than quietly
        // running a shorter scenario.
        report.Errors.AddRange(spec.LoadErrors);
        return report;
    }
}

public static class SuiteReportSerializer
{
    // suiteMode is the caller's launch mode, not a property of the data: a run driven by RWTH_SCENARIO
    // writes the bare single-scenario shape it always has, and a run driven by RWTH_SUITE writes the
    // wrapper even when the list happens to hold one entry. Keying off the launch mode rather than the
    // scenario count means the runner always knows which shape to expect, instead of having to guess
    // from a count it would also have to compute.
    public static string Serialize(SuiteReport suite, bool suiteMode)
    {
        if (!suiteMode && suite.Scenarios.Count == 1)
            return JsonSerializer.Serialize(suite.Scenarios[0]);

        return JsonSerializer.Serialize(suite);
    }
}
