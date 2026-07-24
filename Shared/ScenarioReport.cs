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
}
