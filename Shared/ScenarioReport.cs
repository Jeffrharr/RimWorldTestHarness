using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

public sealed class ProbeCheckResult
{
    public string ProbeName { get; set; } = "";
    public float ActualValue { get; set; }
    public float ExpectedValue { get; set; }
    public float Tolerance { get; set; }
    public bool Pass { get; set; }
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
