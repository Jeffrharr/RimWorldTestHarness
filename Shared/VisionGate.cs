using System.Collections.Generic;
using System.Linq;

namespace RimWorldTestHarness.Shared;

// The policy half of the vision tier: given asserts and whatever verdicts exist, decide what blocks.
// Pure and separate from the data model for the same reason ReportComparer is separate from
// ScenarioReport — the interesting decisions should be edge-case testable without a game or a judge.
public static class VisionGate
{
    // Only a CONFIDENT FAIL blocks. Everything else is advisory.
    //
    // The asymmetry is deliberate. An LLM judging a screenshot is a fallible reviewer, so treating
    // its uncertain opinion as a build gate would train everyone to ignore the gate — the worst
    // possible outcome for a check whose whole value is that someone reads it. A confident "this is
    // broken", though, is exactly the signal that a probe-green run was lying.
    public static VisionOutcome Classify(VisionAssert assert)
    {
        if (assert.Verdict == null)
            return VisionOutcome.Pending;

        if (assert.Verdict.Confidence < assert.ConfidenceGate)
            return VisionOutcome.NeedsHuman;

        return assert.Verdict.Pass ? VisionOutcome.Passed : VisionOutcome.Blocked;
    }

    public static bool Blocks(VisionAssert assert) => Classify(assert) == VisionOutcome.Blocked;

    public static bool AnyBlocks(IReadOnlyList<VisionAssert> asserts) => asserts.Any(Blocks);

    // How many asserts nobody has judged. This is the number that keeps a run honest: a scenario can
    // be probe-green while every one of its rubrics is unanswered, and reporting that as a plain
    // "PASS" would be the exact failure this repo keeps designing against — a green run meaning less
    // than it looks like. Callers are expected to say it out loud rather than quietly round it off.
    public static int PendingCount(IReadOnlyList<VisionAssert> asserts) =>
        asserts.Count(a => Classify(a) == VisionOutcome.Pending);

    public static int NeedsHumanCount(IReadOnlyList<VisionAssert> asserts) =>
        asserts.Count(a => Classify(a) == VisionOutcome.NeedsHuman);

    // True only when every assert has been judged confidently. Lets a consumer tell "fully gated"
    // from "gated on probes alone", which the Pass flag by itself cannot express.
    public static bool ReviewComplete(IReadOnlyList<VisionAssert> asserts) =>
        asserts.All(a => Classify(a) is VisionOutcome.Passed or VisionOutcome.Blocked);

    // One-line summary for the runner and the report, e.g. "2 pending review, 1 needs a human".
    // Empty string when there is nothing to say, so callers can skip printing a line entirely.
    public static string Describe(IReadOnlyList<VisionAssert> asserts)
    {
        if (asserts.Count == 0)
            return "";

        List<string> parts = new List<string>();
        AddIfAny(parts, asserts.Count(a => Classify(a) == VisionOutcome.Blocked), "blocked");
        AddIfAny(parts, PendingCount(asserts), "pending review");
        AddIfAny(parts, NeedsHumanCount(asserts), "needs a human");
        AddIfAny(parts, asserts.Count(a => Classify(a) == VisionOutcome.Passed), "passed");
        return string.Join(", ", parts);
    }

    private static void AddIfAny(List<string> parts, int count, string label)
    {
        if (count > 0)
            parts.Add($"{count} {label}");
    }
}
