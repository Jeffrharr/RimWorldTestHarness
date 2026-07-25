using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// A rubric handed to an LLM judge, plus the evidence it needs to answer: "did this test actually
// work?" — the question a numeric probe cannot ask.
//
// This tier exists because CelestialLighting #15 shipped with unit tests AND a numeric probe both
// green while the effect rendered nothing at all. Only a by-hand off/on comparison caught it. A
// probe checks that a formula returns the right number; nothing checked that the number reached the
// screen.
//
// Deliberately NOT resolved during the run. The harness emits the packet and a Claude session (or
// the RimWorldDevMCP channel) writes a verdict back, rather than the runner calling an LLM inline.
// That keeps the gate free of an API key, a per-run cost and a network dependency — and keeps every
// decision in this file offline-testable, which is the property the whole Shared/ split protects.
public sealed class VisionAssert
{
    // Scenario-unique, so a verdict written later can name which assert it answers. Derived from the
    // step index when the scenario doesn't supply one.
    public string Id { get; set; } = "";

    // The rubric: what the judge is looking at and what would count as correct. Authored per
    // scenario because only the scenario knows what its images are supposed to show.
    public string Prompt { get; set; } = "";

    // One-line summary of the expected outcome. Kept separate from Prompt so a report can list what
    // was expected without reprinting a paragraph.
    public string Expect { get; set; } = "";

    // Below this, a verdict is treated as too uncertain to act on and is flagged for a human instead
    // of blocking. See VisionGate.
    public float ConfidenceGate { get; set; } = DefaultConfidenceGate;

    public const float DefaultConfidenceGate = 0.7f;

    // Absolute paths, in the order the rubric refers to them ("the SECOND frame has the effect on").
    public List<string> ImagePaths { get; set; } = new();

    // Warnings and errors from the game's own log buffer, captured at the moment of the assert. A
    // whole class of failure — a missing def, an exception thrown mid-step, a red error — is
    // invisible in a screenshot but obvious here, and asking the judge to look at a picture while
    // withholding the stack trace next to it would be daft.
    public List<string> LogExcerpt { get; set; } = new();

    // Null until a judge answers. Null is a real state, not a missing value: it means "nobody has
    // looked yet", which the gate must never confuse with "looked and approved".
    public VisionVerdict? Verdict { get; set; }
}

public sealed class VisionVerdict
{
    public bool Pass { get; set; }

    // 0..1. The judge's own confidence, which is what makes this a soft gate: a confident FAIL is
    // worth blocking a run over, an unsure one is worth a human's attention but not a red build.
    public float Confidence { get; set; }

    // One line. Required in practice — a verdict with no reason is unreviewable, and the point of
    // recording verdicts is that the next person doesn't have to re-derive them.
    public string Reason { get; set; } = "";
}

public enum VisionOutcome
{
    // Emitted, nobody has judged it yet. Does not block; the run is provisionally green.
    Pending,

    // Judged fail, confidently. The only outcome that fails a run.
    Blocked,

    // Judged pass, confidently.
    Passed,

    // Judged either way but below the confidence gate. Does not block — it asks for a human.
    NeedsHuman,
}
