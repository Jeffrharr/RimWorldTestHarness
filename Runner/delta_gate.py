#!/usr/bin/env python3
"""Runner/delta_gate.py — turns a scenario's declared delta expectation into a pass or a fail.

WHY THIS IS A SEPARATE STEP AND NOT PART OF THE MOD
---------------------------------------------------
The mod cannot judge its own screenshots. A PNG is not on disk until Unity has flushed it, the
comparison wants a decoder the game does not ship, and doing image work inside the tick loop is a
good way to make the thing under test slower than it is. So the Assert(kind=delta) step does the
half only the game can do — resolve which frames, record what the scenario had declared at that
point — and stops. This script does the half only the runner can do, after the game has exited, and
writes the verdict back into the same report.

That split is why `Pass` in a report is written twice: once by the mod, from everything it knew,
and once here, ANDed with the delta results. This script is the last writer. It never recomputes
the mod's half — it only narrows it — so there is no gate rule living in two languages.

WHY AN UNEVALUATED DELTA ASSERT FAILS
-------------------------------------
A missing ffmpeg, a deleted frame, an unreadable PNG: every one of those ends with a scenario that
declared a hard gate and did not run it. The tempting behaviour is to warn and move on, because the
cause is environmental rather than a real defect. That is exactly the failure this repo keeps
writing rules against — a green run meaning less than it looks like. So an assert that could not be
evaluated gets a Result with Pass=false and a reason naming the cause. Loud and fixable beats quiet
and green.

Note this is the opposite policy from vision asserts (Shared/VisionGate.cs), and deliberately so.
An unjudged vision assert is waiting on a human who may legitimately not have looked yet; an
unevaluated delta assert is waiting on nobody. Nothing will ever arrive to make it green.

    python3 delta_gate.py REPORT.json [--verbose]
"""

import json
import os
import sys

import frame_delta

# Which way the frame is supposed to have moved. `any` asserts only the magnitude band.
#
# Each direction reads ONE number out of the measurement, chosen so the assertion means what its
# English name means:
#
#   brighter/darker   mean signed ΔL* over the sampled pixels. Not mean ΔE, which is unsigned and
#                     would call a frame that got darker "brighter" without blinking.
#   warmer/cooler     nearest correlated colour temperature of the mean colour. Warmer is LOWER in
#                     kelvin — the everyday sense of the word runs backwards to the number, which is
#                     the single easiest thing to get wrong here, hence this note and its test.
#   purpler/greener   signed Duv. Negative is the purple/magenta side of the Planckian locus. This
#                     is the direction a magnitude alone cannot express: a big ΔE that stayed on the
#                     locus is a warmth change wearing a hue change's number.
DIRECTION_ANY = "any"
DIRECTIONS = (DIRECTION_ANY, "brighter", "darker", "warmer", "cooler", "purpler", "greener")


def direction_delta(direction, stats):
    """(signed movement in this direction's own units, unit label). Positive == the direction held."""
    if direction == "brighter":
        return stats["MeanDeltaL"], "ΔL*"
    if direction == "darker":
        return -stats["MeanDeltaL"], "ΔL*"
    if direction == "warmer":
        return stats["CctBefore"] - stats["CctAfter"], "K cooler-to-warmer"
    if direction == "cooler":
        return stats["CctAfter"] - stats["CctBefore"], "K warmer-to-cooler"
    if direction == "purpler":
        return stats["DuvBefore"] - stats["DuvAfter"], "Duv toward purple"
    if direction == "greener":
        return stats["DuvAfter"] - stats["DuvBefore"], "Duv toward green"
    raise ValueError(f"unknown direction {direction!r} (expected one of {', '.join(DIRECTIONS)})")


def judge(stats, direction=DIRECTION_ANY, min_delta_e=None, max_delta_e=None):
    """(pass, reason) for one measurement against one declared expectation. Pure.

    Reasons always carry the numbers, passing or failing. A recorded verdict that says only "FAIL"
    sends the next reader back to re-derive the measurement by hand, which is the habit this whole
    tier exists to break.
    """
    median = stats["MedianDeltaE"]
    failures = []

    if direction != DIRECTION_ANY:
        moved, unit = direction_delta(direction, stats)
        if moved <= 0:
            failures.append(f"not {direction}: moved {moved:+.4g} {unit}")

    if min_delta_e is not None and median < min_delta_e:
        failures.append(f"median ΔE {median:.2f} below the declared minimum {min_delta_e:g}")

    if max_delta_e is not None and median > max_delta_e:
        failures.append(f"median ΔE {median:.2f} above the declared maximum {max_delta_e:g}")

    summary = (f"median ΔE {median:.2f} ({stats['Verdict']}), "
               f"{stats['ChangedFraction'] * 100:.1f}% of sampled pixels changed, "
               f"mean ΔL* {stats['MeanDeltaL']:+.2f}, "
               f"Duv {stats['DuvBefore']:+.5f} -> {stats['DuvAfter']:+.5f}")

    if failures:
        return False, "; ".join(failures) + f" [{summary}]"
    return True, summary


# ---------------------------------------------------------------------------------------------
# Report traversal. PascalCase keys throughout: Shared/SuiteReport.cs serializes with
# System.Text.Json's default naming policy (i.e. none), so these are the C# property names verbatim.
# ---------------------------------------------------------------------------------------------


def scenarios_of(report):
    """Both report shapes as one list. A suite wraps N scenarios; a single run IS a scenario."""
    return report["Scenarios"] if "Scenarios" in report else [report]


def measure(assert_):
    """(stats or None, error or None) for one delta assert. The only impure part of the gate."""
    baseline = assert_.get("BaselinePath", "")
    target = assert_.get("TargetPath", "")

    for path in (baseline, target):
        if not os.path.exists(path):
            return None, (f"no frame at {path} — the assert named a screenshot that is not on disk "
                          "(deleted by --delete-frames, or never captured)")

    try:
        stats = frame_delta.compare_files(
            baseline, target,
            assert_.get("Region") or frame_delta.FULL_REGION,
            int(assert_.get("Stride") or 2))
    except FileNotFoundError:
        # Raised by subprocess when ffmpeg/ffprobe themselves are absent, which is a box-setup
        # problem rather than a bad assert — worth saying so in as many words.
        return None, ("could not decode the frames — ffmpeg and ffprobe must be on PATH for delta "
                      "asserts to be evaluated")
    except Exception as error:  # noqa: BLE001 - any decode/geometry failure is reported, not raised
        return None, f"could not measure: {error}"

    return stats, None


def evaluate_assert(assert_):
    """Fill in one assert's Result in place. Returns whether it passed."""
    stats, error = measure(assert_)
    if error is not None:
        assert_["Result"] = {"Pass": False, "Reason": error, "Stats": None}
        return False

    passed, reason = judge(
        stats,
        assert_.get("Direction") or DIRECTION_ANY,
        assert_.get("MinDeltaE"),
        assert_.get("MaxDeltaE"))
    assert_["Result"] = {"Pass": passed, "Reason": reason, "Stats": stats}
    return passed


def evaluate_scenario(scenario):
    """Evaluate every unjudged delta assert in one scenario and narrow its Pass. Returns a summary."""
    asserts = scenario.get("DeltaAsserts") or []
    results = []
    for assert_ in asserts:
        # An assert that already carries a Result is left alone, so re-running the gate over a
        # report is idempotent and a hand-written verdict is never silently overwritten.
        if assert_.get("Result") is None:
            evaluate_assert(assert_)
        results.append(assert_)

    # AND, never recompute. The mod's Pass already folded in probe checks, step errors and blocking
    # vision verdicts; re-deriving any of that here would put the same rule in two languages and
    # give it two chances to drift.
    if results:
        scenario["Pass"] = bool(scenario.get("Pass")) and all(a["Result"]["Pass"] for a in results)

    return results


def evaluate_report(path, verbose=False):
    """Judge every delta assert in a report file, rewriting it in place. Returns the exit code."""
    with open(path, encoding="utf-8") as handle:
        report = json.load(handle)

    scenarios = scenarios_of(report)
    judged = []
    for scenario in scenarios:
        judged.extend(evaluate_scenario(scenario))

    if not judged:
        return EXIT_ALL_PASSED

    # A suite's own Pass is the AND of its scenarios' — mirrored here only because this script may
    # have just flipped one of them to false, and a suite reporting Pass=true over a failed scenario
    # would be worse than either half being wrong on its own.
    if "Scenarios" in report:
        report["Pass"] = bool(report.get("Pass")) and all(s.get("Pass") for s in scenarios)

    with open(path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)

    if verbose:
        for assert_ in judged:
            state = "PASS" if assert_["Result"]["Pass"] else "FAIL"
            print(f"delta {assert_.get('Id')}: {state} — {assert_['Result']['Reason']}")

    return EXIT_ALL_PASSED if all(a["Result"]["Pass"] for a in judged) else EXIT_ASSERT_FAILED


# Exit codes are three-way on purpose. 0 and 1 are both SUCCESSFUL runs of the gate — the verdict for
# each assert is in the report either way, and run_test.sh reads it there rather than from here. 2
# means the gate itself could not run, which is the case a caller must not confuse with "nothing
# failed": a traceback that exits 1 like a failing assert does would be indistinguishable from a
# clean report, and the run would go green over a gate that never happened.
EXIT_ALL_PASSED = 0
EXIT_ASSERT_FAILED = 1
EXIT_GATE_BROKEN = 2


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    verbose = "--verbose" in argv
    paths = [a for a in argv if not a.startswith("--")]
    if len(paths) != 1:
        raise SystemExit(__doc__)

    try:
        return evaluate_report(paths[0], verbose)
    except Exception as error:  # noqa: BLE001 - see EXIT_GATE_BROKEN
        print(f"delta gate could not run: {error}", file=sys.stderr)
        return EXIT_GATE_BROKEN


if __name__ == "__main__":
    sys.exit(main())
