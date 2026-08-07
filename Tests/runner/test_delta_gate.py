#!/usr/bin/env python3
"""Offline tests for Runner/delta_gate.py.

The measurement is frame_delta's job and is tested next door; what is tested here is the JUDGEMENT —
which measured number each direction reads, which way each one has to move, and what happens to a
report when an assert cannot be evaluated at all. Those are the decisions that turn a number into a
verdict, and a wrong one produces a run that is confidently green about the opposite of what the
scenario asked for.

No ffmpeg: the measuring call is stubbed, because none of these cases are about decoding a PNG.

Run: python3 -m unittest discover -s Tests/runner   (or via ./test.sh)
"""

import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Runner"))

import delta_gate  # noqa: E402
import frame_delta  # noqa: E402


def stats(median=3.0, mean_delta_l=0.0, cct_before=5000.0, cct_after=5000.0,
          duv_before=0.0, duv_after=0.0, changed=0.5):
    """A measurement shaped exactly like compare_buffers', with only the judged fields set."""
    return {
        "MedianDeltaE": median,
        "MeanDeltaE": median,
        "P90DeltaE": median,
        "P99DeltaE": median,
        "ChangedFraction": changed,
        "Verdict": frame_delta.verdict_for(median),
        "MeanDeltaL": mean_delta_l,
        "MeanColorBefore": [10, 10, 10],
        "MeanColorAfter": [20, 20, 20],
        "DuvBefore": duv_before,
        "DuvAfter": duv_after,
        "CctBefore": cct_before,
        "CctAfter": cct_after,
    }


class Directions(unittest.TestCase):
    def test_any_asserts_only_the_magnitude(self):
        passed, _ = delta_gate.judge(stats(median=3.0, mean_delta_l=-40.0), min_delta_e=2.0)
        self.assertTrue(passed, "a bare magnitude assert must not care which way the frame moved")

    def test_brighter_and_darker_read_the_signed_lightness(self):
        brighter = stats(mean_delta_l=+5.0)
        darker = stats(mean_delta_l=-5.0)
        self.assertTrue(delta_gate.judge(brighter, "brighter")[0])
        self.assertFalse(delta_gate.judge(brighter, "darker")[0])
        self.assertTrue(delta_gate.judge(darker, "darker")[0])
        self.assertFalse(delta_gate.judge(darker, "brighter")[0])

    def test_a_frame_that_did_not_move_fails_both_lightness_directions(self):
        # Zero is not "in the direction asked for". A scenario declaring "brighter" over a frame that
        # did not change at all is the exact failure this tier was built to catch.
        flat = stats(mean_delta_l=0.0)
        self.assertFalse(delta_gate.judge(flat, "brighter")[0])
        self.assertFalse(delta_gate.judge(flat, "darker")[0])

    def test_warmer_means_a_lower_colour_temperature(self):
        # Backwards to the everyday word, and the thing most likely to be got wrong here.
        warmed = stats(cct_before=6000.0, cct_after=3000.0)
        self.assertTrue(delta_gate.judge(warmed, "warmer")[0])
        self.assertFalse(delta_gate.judge(warmed, "cooler")[0])

    def test_cooler_means_a_higher_colour_temperature(self):
        cooled = stats(cct_before=3000.0, cct_after=6000.0)
        self.assertTrue(delta_gate.judge(cooled, "cooler")[0])
        self.assertFalse(delta_gate.judge(cooled, "warmer")[0])

    def test_purpler_means_duv_fell_and_greener_means_it_rose(self):
        purpled = stats(duv_before=+0.001, duv_after=-0.004)
        greened = stats(duv_before=-0.004, duv_after=+0.001)
        self.assertTrue(delta_gate.judge(purpled, "purpler")[0])
        self.assertFalse(delta_gate.judge(purpled, "greener")[0])
        self.assertTrue(delta_gate.judge(greened, "greener")[0])
        self.assertFalse(delta_gate.judge(greened, "purpler")[0])

    def test_a_big_magnitude_that_stayed_on_the_locus_is_not_a_hue_change(self):
        # The failure a magnitude alone cannot catch: ΔE 40 is unmistakably "obvious", and the frame
        # only got warmer. A subsystem whose claim is a HUE must not pass on this.
        warmth_only = stats(median=40.0, cct_before=6000.0, cct_after=2500.0,
                            duv_before=0.0, duv_after=0.0)
        self.assertTrue(delta_gate.judge(warmth_only, min_delta_e=5.0)[0])
        self.assertFalse(delta_gate.judge(warmth_only, "purpler", min_delta_e=5.0)[0])

    def test_unknown_direction_is_an_error(self):
        with self.assertRaises(ValueError):
            delta_gate.judge(stats(), "sideways")

    def test_every_declared_direction_is_judgeable(self):
        # Keeps this list and Shared/Steps/BuiltIn/AssertStep.Directions honest with each other: the
        # step validates against its copy, and a name it accepted that the gate cannot read would
        # fail after a run had already spent its frames.
        for direction in delta_gate.DIRECTIONS:
            delta_gate.judge(stats(), direction)


class Bounds(unittest.TestCase):
    def test_below_the_floor_fails(self):
        passed, reason = delta_gate.judge(stats(median=0.4), min_delta_e=2.0)
        self.assertFalse(passed)
        self.assertIn("0.40", reason)
        self.assertIn("minimum 2", reason)

    def test_above_the_ceiling_fails(self):
        # The opposite failure to the usual one: an effect that should have been subtle blew out the
        # whole frame. Worth gating because "more difference" is not automatically better.
        passed, reason = delta_gate.judge(stats(median=60.0), max_delta_e=10.0)
        self.assertFalse(passed)
        self.assertIn("maximum 10", reason)

    def test_inside_a_band_passes(self):
        self.assertTrue(delta_gate.judge(stats(median=6.0), min_delta_e=2.0, max_delta_e=10.0)[0])

    def test_bounds_are_inclusive(self):
        self.assertTrue(delta_gate.judge(stats(median=2.0), min_delta_e=2.0)[0])
        self.assertTrue(delta_gate.judge(stats(median=2.0), max_delta_e=2.0)[0])

    def test_both_failures_are_reported_not_just_the_first(self):
        passed, reason = delta_gate.judge(stats(median=0.1, mean_delta_l=-3.0),
                                          "brighter", min_delta_e=2.0)
        self.assertFalse(passed)
        self.assertIn("not brighter", reason)
        self.assertIn("minimum", reason)


class Reasons(unittest.TestCase):
    """A verdict with no numbers sends the next reader back to re-derive the measurement by hand."""

    def test_a_passing_reason_still_carries_the_measurement(self):
        _, reason = delta_gate.judge(stats(median=6.79, changed=0.37), min_delta_e=2.0)
        self.assertIn("6.79", reason)
        self.assertIn("37.0%", reason)
        self.assertIn("obvious", reason)

    def test_a_failing_reason_carries_it_too(self):
        _, reason = delta_gate.judge(stats(median=0.2, duv_before=0.001, duv_after=0.002),
                                     min_delta_e=2.0)
        self.assertIn("0.20", reason)
        self.assertIn("Duv", reason)


class ReportFixture(unittest.TestCase):
    """Report round-trips, with the measuring call stubbed so no ffmpeg is involved."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="delta_gate-test-")
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.real_compare = frame_delta.compare_files
        self.addCleanup(setattr, frame_delta, "compare_files", self.real_compare)
        self.measured = stats(median=6.0, mean_delta_l=+4.0)
        frame_delta.compare_files = lambda *a, **k: dict(self.measured)

    def frame(self, name):
        path = os.path.join(self.tmp, name)
        with open(path, "wb") as handle:
            handle.write(b"not really a png, and nothing here decodes it")
        return path

    def an_assert(self, **overrides):
        packet = {
            "Id": "s#delta0",
            "BaselinePath": self.frame("off.png"),
            "TargetPath": self.frame("on.png"),
            "Region": "full",
            "Stride": 2,
            "Direction": "brighter",
            "MinDeltaE": 2.0,
            "MaxDeltaE": None,
            "Expect": "the lights come on",
            "Inputs": ["SetFeature(enabled=true, featureName=lights)"],
            "Result": None,
        }
        packet.update(overrides)
        return packet

    def write(self, report):
        path = os.path.join(self.tmp, "report.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(report, handle)
        return path

    def read(self, path):
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)


class ScenarioEvaluation(ReportFixture):
    def test_a_passing_delta_leaves_a_passing_scenario_green(self):
        scenario = {"ScenarioName": "s", "Pass": True, "DeltaAsserts": [self.an_assert()]}
        delta_gate.evaluate_scenario(scenario)
        self.assertTrue(scenario["Pass"])
        self.assertTrue(scenario["DeltaAsserts"][0]["Result"]["Pass"])

    def test_a_failing_delta_turns_a_passing_scenario_red(self):
        scenario = {"ScenarioName": "s", "Pass": True,
                    "DeltaAsserts": [self.an_assert(Direction="darker")]}
        delta_gate.evaluate_scenario(scenario)
        self.assertFalse(scenario["Pass"])

    def test_a_passing_delta_cannot_rescue_a_scenario_that_already_failed(self):
        # The gate ANDs; it never recomputes. Re-deriving the mod's half here would put the same rule
        # in two languages and give it two chances to drift.
        scenario = {"ScenarioName": "s", "Pass": False, "DeltaAsserts": [self.an_assert()]}
        delta_gate.evaluate_scenario(scenario)
        self.assertFalse(scenario["Pass"])

    def test_a_scenario_with_no_delta_asserts_is_untouched(self):
        scenario = {"ScenarioName": "s", "Pass": True}
        delta_gate.evaluate_scenario(scenario)
        self.assertTrue(scenario["Pass"])

    def test_the_measurement_is_recorded_alongside_the_verdict(self):
        # Not decoration. A ΔE with no record of what produced it is how a real defect hid for a
        # whole PR cycle; the stats and the declared inputs are where an unexplained result points.
        scenario = {"ScenarioName": "s", "Pass": True, "DeltaAsserts": [self.an_assert()]}
        delta_gate.evaluate_scenario(scenario)
        result = scenario["DeltaAsserts"][0]["Result"]
        self.assertEqual(result["Stats"]["MedianDeltaE"], 6.0)
        self.assertEqual(scenario["DeltaAsserts"][0]["Inputs"],
                         ["SetFeature(enabled=true, featureName=lights)"])

    def test_an_existing_result_is_not_overwritten(self):
        # Makes re-running the gate over a report idempotent, and keeps a hand-written verdict.
        verdict = {"Pass": True, "Reason": "judged by hand", "Stats": None}
        scenario = {"ScenarioName": "s", "Pass": True,
                    "DeltaAsserts": [self.an_assert(Result=verdict)]}
        delta_gate.evaluate_scenario(scenario)
        self.assertEqual(scenario["DeltaAsserts"][0]["Result"]["Reason"], "judged by hand")


class Unevaluable(ReportFixture):
    """An assert that could not be measured FAILS. It is not pending; nothing is waiting on it."""

    def test_a_missing_frame_fails_and_names_the_path(self):
        packet = self.an_assert(TargetPath=os.path.join(self.tmp, "never-captured.png"))
        scenario = {"ScenarioName": "s", "Pass": True, "DeltaAsserts": [packet]}
        delta_gate.evaluate_scenario(scenario)

        self.assertFalse(scenario["Pass"])
        self.assertFalse(packet["Result"]["Pass"])
        self.assertIn("never-captured.png", packet["Result"]["Reason"])
        self.assertIsNone(packet["Result"]["Stats"])

    def test_a_missing_ffmpeg_fails_and_says_so(self):
        def no_ffmpeg(*a, **k):
            raise FileNotFoundError("ffprobe")

        frame_delta.compare_files = no_ffmpeg
        packet = self.an_assert()
        scenario = {"ScenarioName": "s", "Pass": True, "DeltaAsserts": [packet]}
        delta_gate.evaluate_scenario(scenario)

        self.assertFalse(scenario["Pass"])
        self.assertIn("ffmpeg", packet["Result"]["Reason"])

    def test_frames_of_different_sizes_fail_rather_than_raise(self):
        def mismatched(*a, **k):
            raise ValueError("frame sizes differ: 640x360 vs 800x600")

        frame_delta.compare_files = mismatched
        packet = self.an_assert()
        scenario = {"ScenarioName": "s", "Pass": True, "DeltaAsserts": [packet]}
        delta_gate.evaluate_scenario(scenario)

        self.assertFalse(packet["Result"]["Pass"])
        self.assertIn("640x360", packet["Result"]["Reason"])


class ReportShapes(ReportFixture):
    def test_a_single_scenario_report_is_its_own_scenario(self):
        path = self.write({"ScenarioName": "s", "Pass": True, "DeltaAsserts": [self.an_assert()]})
        self.assertEqual(delta_gate.evaluate_report(path), delta_gate.EXIT_ALL_PASSED)
        self.assertTrue(self.read(path)["DeltaAsserts"][0]["Result"]["Pass"])

    def test_a_failing_delta_in_a_suite_reddens_both_scenario_and_suite(self):
        path = self.write({
            "Pass": True,
            "Scenarios": [
                {"ScenarioName": "ok", "Pass": True, "DeltaAsserts": []},
                {"ScenarioName": "bad", "Pass": True,
                 "DeltaAsserts": [self.an_assert(Direction="darker")]},
            ],
        })
        self.assertEqual(delta_gate.evaluate_report(path), delta_gate.EXIT_ASSERT_FAILED)

        report = self.read(path)
        self.assertFalse(report["Pass"])
        self.assertTrue(report["Scenarios"][0]["Pass"])
        self.assertFalse(report["Scenarios"][1]["Pass"])

    def test_a_report_with_no_delta_asserts_is_not_rewritten(self):
        # Byte-for-byte: a gate with nothing to do must not reformat a report and make every later
        # diff of it noise.
        path = self.write({"ScenarioName": "s", "Pass": True, "DeltaAsserts": []})
        with open(path, "rb") as handle:
            before = handle.read()

        self.assertEqual(delta_gate.evaluate_report(path), delta_gate.EXIT_ALL_PASSED)

        with open(path, "rb") as handle:
            self.assertEqual(handle.read(), before)

    def test_a_broken_report_exits_distinctly_from_a_failing_assert(self):
        # The distinction that keeps a gate that never ran from looking like a gate that passed.
        path = os.path.join(self.tmp, "not-json.json")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write("{ this is not json")
        self.assertEqual(delta_gate.main([path]), delta_gate.EXIT_GATE_BROKEN)

    def test_the_cli_reports_a_failing_assert_as_exit_one(self):
        path = self.write({"ScenarioName": "s", "Pass": True,
                           "DeltaAsserts": [self.an_assert(Direction="darker")]})
        self.assertEqual(delta_gate.main([path]), delta_gate.EXIT_ASSERT_FAILED)


if __name__ == "__main__":
    unittest.main()
