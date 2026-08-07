using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// How a ProbeCheckResult's actual was compared to its expected. Strings rather than an enum because
// this travels through System.Text.Json into a report that run_test.sh's inline Python reads, and an
// enum would arrive there as an integer nobody can interpret without this file open.
public static class ProbeComparison
{
    // |actual - expected| <= tolerance. What every Probe step has always done, and the default for a
    // record written without a comparison — which is what keeps reports produced before ProfileAssert
    // existed readable by the same code.
    public const string Within = "within";

    public const string AtMost = "atMost";
    public const string AtLeast = "atLeast";
}

public sealed class ProbeCheckResult
{
    public string ProbeName { get; set; } = "";
    public float ActualValue { get; set; }
    public float ExpectedValue { get; set; }
    public float Tolerance { get; set; }
    public bool Pass { get; set; }

    // One of ProbeComparison.*. Added for ProfileAssert, whose bounds are one-sided far more often
    // than they are two-sided: "this patch must cost at most 1 ms/frame" is the assertion people
    // actually want, and expressing it as expected±tolerance loses which end was being defended the
    // moment anyone reads the report back.
    public string Comparison { get; set; } = ProbeComparison.Within;
}

// Written by the Mod at the end of a scenario run, read by Runner/run_test.sh (exit-code gate)
// and by whoever/whatever reviews ScreenshotPaths afterward. One report per run — the Mod writes
// it to a path the Runner passes in (env var or command-line arg; TBD in the Mod implementation).
public sealed class ScenarioReport
{
    public string ScenarioName { get; set; } = "";
    public bool Pass { get; set; }
    public List<ProbeCheckResult> ProbeChecks { get; set; } = new();
    public List<string> ScreenshotPaths { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    // Rubrics emitted for an LLM judge, with their verdicts once someone has answered them. Only a
    // confident FAIL affects Pass (see VisionGate); an unjudged one leaves the run provisionally
    // green, which is why the runner prints the pending count rather than rounding it off.
    public List<VisionAssert> VisionAsserts { get; set; } = new();

    // Per-patch cost tables: one written for every scenario of a profiled run (see
    // Shared/RunProfiling.cs), plus one per explicit Profile/ProfileStop step. Informational on their
    // own — only a ProfileAssert step turns one of these numbers into something that gates Pass —
    // because the primary use is diffing the same table between two builds, and a run that went red
    // merely because the machine was busy would train everyone to ignore the colour.
    public List<ProfileTable> Profiles { get; set; } = new();

    // Whether Dubs Performance Analyzer was instrumenting the load while THIS scenario ran.
    //
    // THE GUARDRAIL. Profiling rewrites the body of every Harmony-patched method in the load, so every
    // timing number in a profiled run — ordinary Probe steps included, not just profile tables — is
    // measured through an instrumented build. Pin a probe's expectedValue from a profiled run, compare
    // it against an unprofiled one, and the check moves for a reason that has nothing to do with the
    // code under test. This flag is what makes that visible after the fact: it rides on every report,
    // and Runner/run_test.sh prints it next to pass/fail.
    //
    // It is deliberately only HALF the mitigation, because a marker relies on a human noticing a line
    // of output. The other half is ProbePinning: a Probe step may record which mode its own expected
    // value was pinned under, and a mismatch is recorded as an Error here rather than a note in the
    // margin. See Shared/RunProfiling.cs.
    public bool Profiled { get; set; }

    // Why a profiled run produced no table for this scenario. Non-null is a LOUD no-measurement: no map
    // was ever loaded, no frames elapsed, the window was too short to mean anything, or nothing
    // instrumented ran. Recorded — and printed by the runner — rather than left to a table of zeroes,
    // which is a number that looks like a measurement, means "nothing was measured", and reads as
    // "this mod is free". See RunProfiling.AfterWindowSkipReason for the full list of causes.
    //
    // Distinct from Skipped/SkipReason below, which are about the SCENARIO being inapplicable. A
    // scenario whose profiling was skipped still ran, still asserted and still gates Pass as normal;
    // only its cost table is absent.
    public string? ProfileSkipReason { get; set; }

    // Set when a step declared the scenario inapplicable to this install rather than failed — today
    // only LandInOrbit without Odyssey. The remaining steps are abandoned and Pass is computed as
    // usual (no errors, no failed probes => true), so a harness run on a box without the DLC stays
    // green instead of red over something nobody can fix by editing a mod.
    //
    // A flag of its own rather than "an error that doesn't count", because a skip IS the case where a
    // green result means less than it looks like, and the only mitigation available is saying so
    // wherever the result is read: Player.log, this report and run_test.sh's summary all print
    // SKIPPED rather than pass=True. Anything recorded before the skip still gates Pass as normal —
    // skipping stops a scenario, it does not absolve it.
    public bool Skipped { get; set; }

    public string? SkipReason { get; set; }
}
