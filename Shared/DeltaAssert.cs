using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// An automated pixel comparison between two of a scenario's own screenshots: the second verification
// tier issue #7 calls `expectDelta`, sitting between a numeric Probe (deterministic, hard) and a
// vision assert (an LLM's opinion, soft).
//
// WHY THIS TIER EXISTS. A probe checks that a formula returned the right number. Nothing checked
// that the number reached the screen — which is how CelestialLighting #15 shipped green while
// rendering nothing at all. Absolute pixels are hopeless (the colony, weather, pawns and HUD are
// random per boot), but two frames from ONE boot with one feature toggled between them differ only
// by that feature, by construction. The delta is stable even though neither frame is.
//
// WHY IT IS ONLY HALF FILLED IN HERE. The game resolves which frames and records what it declared;
// it does not open the PNGs. Unity has not necessarily flushed them, the comparison wants a decoder
// the game does not ship, and doing image work inside the tick loop would slow down the thing being
// measured. Runner/delta_gate.py fills in Result after the game exits and ANDs it into Pass. See
// that file for why an assert it could not evaluate is a FAILURE rather than a warning.
public sealed class DeltaAssert
{
    // Scenario-unique, so a result written later names which assert it answers. Derived from the
    // step index when the scenario doesn't supply one.
    public string Id { get; set; } = "";

    // Absolute paths to the two frames, in the order "before" then "after". Absolute rather than
    // fileNames because the gate runs from a different working directory than the game did, and in
    // suite mode the on-disk name is scenario-qualified (see SuiteScreenshots) — resolving it twice
    // is exactly the drift that mirror is warned about.
    public string BaselinePath { get; set; } = "";
    public string TargetPath { get; set; } = "";

    // "full" (default) or "X,Y,W,H" in pixels. Whole-frame is right for the ordinary shape — toggle
    // one feature, everything else is identical by construction, so the untouched majority
    // contributes exact zeros and cannot invent a difference. A rect is for the case where the
    // scenario legitimately changes the scene between captures, at which point "everything else is
    // identical" stops being true and a whole-frame number is partly about the change nobody asked
    // about.
    public string Region { get; set; } = "full";

    // Sample every Nth pixel. Subsampling, not downscaling: averaging neighbours first would smooth
    // away the per-pixel differences being measured.
    public int Stride { get; set; } = 2;

    // any (default) | brighter | darker | warmer | cooler | purpler | greener. See
    // Runner/delta_gate.py for which measured number each one reads, and the note there about
    // "warmer" being LOWER in kelvin.
    public string Direction { get; set; } = "any";

    // The magnitude band, in median CIELAB ΔE. Null means unbounded on that end. The usual shape is
    // a floor alone ("this must be visible at all"); a ceiling catches the opposite failure, where a
    // change that should be subtle has blown out the whole frame.
    public float? MinDeltaE { get; set; }
    public float? MaxDeltaE { get; set; }

    // One-line summary of what the scenario expects, for a report to print without restating the
    // bounds. Same role as VisionAssert.Expect.
    public string Expect { get; set; } = "";

    // WHAT THE SCENARIO DECLARED BETWEEN THE TWO FRAMES — the steps that ran after the baseline
    // screenshot and before the target one, rendered as text.
    //
    // This is here because of a live failure that cost a whole PR cycle. A scenario probed ONE of
    // the two values that determined its result and not the other, so two runs reporting an
    // identical probe value produced an eightfold difference on screen, with nothing in the report
    // to explain it. The report was not wrong; it was incomplete in a way that looked complete.
    //
    // A ΔE with no record of what produced it has exactly that gap. These lines are the cheapest
    // honest fix: whatever the number turns out to be, the reader can see which declared inputs were
    // in play for that specific comparison instead of reconstructing them from the scenario file.
    //
    // Deliberately scoped to BETWEEN the two frames rather than the whole scenario. What the two
    // captures share cannot explain how they differ, and a full step dump would bury the two or
    // three lines that can.
    public List<string> Inputs { get; set; } = new();

    // Null until Runner/delta_gate.py has run. Unlike a vision verdict, null here is never a
    // legitimate resting state — nothing is waiting on a human — so the runner treats an assert
    // still holding null as a failure rather than as pending.
    public DeltaResult? Result { get; set; }
}

public sealed class DeltaResult
{
    public bool Pass { get; set; }

    // Always carries the numbers, passing or failing. A recorded verdict that says only "FAIL" sends
    // the next reader back to re-derive the measurement by hand, which is the habit this tier exists
    // to break.
    public string Reason { get; set; } = "";

    // Every statistic the comparison produced, not only the one that was asserted on. Written as a
    // free-form bag because the gate that fills it in is Python and the schema is its business (see
    // Runner/frame_delta.py's compare_buffers), and because the useful thing to do with it is read
    // it or diff it between builds — not branch on it in C#, which nothing here does.
    //
    // Null when the assert could not be measured at all; Reason then names the cause.
    public Dictionary<string, object>? Stats { get; set; }
}
