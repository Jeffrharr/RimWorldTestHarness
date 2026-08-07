#!/usr/bin/env python3
"""Offline tests for Runner/frame_delta.py.

No game, no run, no ffmpeg: every case here builds its own rgb24 buffer and checks the arithmetic
against a value DERIVED from the colour-science definitions, not one recorded from a run. That
distinction is the whole point. A test that pins whatever the code printed last Tuesday will happily
keep passing after the formula rots — and this module exists precisely because a green check that
was not actually checking anything let a broken effect ship.

Reference values below (L* of mid grey, Lab of pure sRGB red, ΔE black-to-white) are published
constants of CIELAB under D65, so they fail if the transform drifts rather than if the output does.

Run: python3 -m unittest discover -s Tests/runner   (or via ./test.sh)
"""

import math
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Runner"))

import frame_delta  # noqa: E402


def buffer_of(width, height, pixel):
    """A solid rgb24 buffer."""
    return bytes(bytes(pixel) * (width * height))


def buffer_from(width, height, pixel_at):
    """An rgb24 buffer whose pixels come from pixel_at(x, y)."""
    out = bytearray()
    for y in range(height):
        for x in range(width):
            out.extend(pixel_at(x, y))
    return bytes(out)


class LabTransform(unittest.TestCase):
    """The CIELAB conversion, against published D65 reference values."""

    def test_white_is_l100_and_neutral(self):
        lightness, a, b = frame_delta.to_lab(255, 255, 255)
        self.assertAlmostEqual(lightness, 100.0, places=3)
        self.assertAlmostEqual(a, 0.0, places=2)
        self.assertAlmostEqual(b, 0.0, places=2)

    def test_black_is_l0(self):
        self.assertEqual(frame_delta.to_lab(0, 0, 0), (0.0, 0.0, 0.0))

    def test_mid_grey_is_the_published_l53_585(self):
        # sRGB 128 is NOT L*50: the transfer function is not a square root. Getting this wrong is the
        # classic way a "perceptual" metric quietly becomes a gamma-encoded one.
        self.assertAlmostEqual(frame_delta.to_lab(128, 128, 128)[0], 53.585, places=2)

    def test_pure_red_matches_the_published_lab(self):
        lightness, a, b = frame_delta.to_lab(255, 0, 0)
        self.assertAlmostEqual(lightness, 53.24, places=1)
        self.assertAlmostEqual(a, 80.09, places=1)
        self.assertAlmostEqual(b, 67.20, places=1)


class Verdicts(unittest.TestCase):
    """The standard perceptual bands, including which side of each boundary a value falls on."""

    def test_bands(self):
        self.assertEqual(frame_delta.verdict_for(0.0), "imperceptible")
        self.assertEqual(frame_delta.verdict_for(0.99), "imperceptible")
        self.assertEqual(frame_delta.verdict_for(1.0), "visible on close inspection")
        self.assertEqual(frame_delta.verdict_for(1.99), "visible on close inspection")
        self.assertEqual(frame_delta.verdict_for(2.0), "visible at a glance")
        self.assertEqual(frame_delta.verdict_for(4.99), "visible at a glance")
        self.assertEqual(frame_delta.verdict_for(5.0), "obvious")
        self.assertEqual(frame_delta.verdict_for(1000.0), "obvious")


class RegionParsing(unittest.TestCase):
    def test_full_and_blank_mean_the_whole_frame(self):
        for raw in (None, "", "  ", "full", "FULL"):
            self.assertEqual(frame_delta.parse_region(raw, 640, 360),
                             frame_delta.Region(0, 0, 640, 360), raw)

    def test_explicit_rect(self):
        self.assertEqual(frame_delta.parse_region("10,20,30,40", 640, 360),
                         frame_delta.Region(10, 20, 30, 40))

    def test_out_of_bounds_is_an_error_not_a_clamp(self):
        # A clamped rect still produces a number, and a number measured over a rect other than the
        # one the scenario asked for is exactly the silent wrongness this tier exists to prevent.
        with self.assertRaises(ValueError):
            frame_delta.parse_region("600,0,100,10", 640, 360)
        with self.assertRaises(ValueError):
            frame_delta.parse_region("0,300,10,100", 640, 360)

    def test_malformed_rects_are_refused(self):
        for raw in ("1,2,3", "1,2,3,4,5", "a,b,c,d", "0,0,0,10", "0,0,10,-1", "-1,0,10,10"):
            with self.assertRaises(ValueError, msg=raw):
                frame_delta.parse_region(raw, 640, 360)


class IdenticalFrames(unittest.TestCase):
    """The case every assertion is measured against: two frames that are the same."""

    def setUp(self):
        frame = buffer_of(8, 8, (90, 120, 200))
        self.stats = frame_delta.compare_buffers(frame, frame, 8, 8, stride=1)

    def test_every_magnitude_is_zero(self):
        for key in ("MedianDeltaE", "MeanDeltaE", "P90DeltaE", "P99DeltaE",
                    "ChangedFraction", "MeanDeltaL"):
            self.assertEqual(self.stats[key], 0.0, key)

    def test_verdict_is_imperceptible(self):
        self.assertEqual(self.stats["Verdict"], "imperceptible")

    def test_mean_colours_agree(self):
        self.assertEqual(self.stats["MeanColorBefore"], self.stats["MeanColorAfter"])
        self.assertEqual(self.stats["MeanColorBefore"], [90, 120, 200])


class Magnitudes(unittest.TestCase):
    def test_black_to_white_is_delta_e_100(self):
        stats = frame_delta.compare_buffers(
            buffer_of(4, 4, (0, 0, 0)), buffer_of(4, 4, (255, 255, 255)), 4, 4, stride=1)
        self.assertAlmostEqual(stats["MedianDeltaE"], 100.0, places=3)
        self.assertAlmostEqual(stats["MeanDeltaE"], 100.0, places=3)
        self.assertEqual(stats["ChangedFraction"], 1.0)
        self.assertEqual(stats["Verdict"], "obvious")

    def test_delta_e_is_symmetric(self):
        black, white = buffer_of(4, 4, (0, 0, 0)), buffer_of(4, 4, (255, 255, 255))
        forward = frame_delta.compare_buffers(black, white, 4, 4, stride=1)
        backward = frame_delta.compare_buffers(white, black, 4, 4, stride=1)
        self.assertAlmostEqual(forward["MedianDeltaE"], backward["MedianDeltaE"], places=9)

    def test_median_ignores_a_localized_change_the_mean_reports(self):
        # THE reason the median is the headline number. Two pixels of four change by exactly 100;
        # the sorted deltas are [0, 0, 100, 100], so median = deltas[2] = 100 while a change in only
        # ONE of four gives [0, 0, 0, 100] -> median 0 and mean 25. A metric that led with the mean
        # would report a quarter of a full-frame inversion as "25", a number that sounds like a
        # moderate global shift and is nothing of the kind.
        def one_of_four(x, y):
            return (255, 255, 255) if (x, y) == (0, 0) else (0, 0, 0)

        stats = frame_delta.compare_buffers(
            buffer_of(2, 2, (0, 0, 0)), buffer_from(2, 2, one_of_four), 2, 2, stride=1)

        self.assertEqual(stats["MedianDeltaE"], 0.0)
        self.assertAlmostEqual(stats["MeanDeltaE"], 25.0, places=3)
        self.assertEqual(stats["ChangedFraction"], 0.25)
        # The tail is what distinguishes "a localized effect" from "no effect at all", which is why
        # it is reported next to a median of zero rather than instead of it.
        self.assertAlmostEqual(stats["P99DeltaE"], 100.0, places=3)

    def test_mean_delta_l_is_signed(self):
        dark, bright = buffer_of(4, 4, (40, 40, 40)), buffer_of(4, 4, (200, 200, 200))
        brighter = frame_delta.compare_buffers(dark, bright, 4, 4, stride=1)
        darker = frame_delta.compare_buffers(bright, dark, 4, 4, stride=1)

        self.assertGreater(brighter["MeanDeltaL"], 0.0)
        self.assertAlmostEqual(brighter["MeanDeltaL"], -darker["MeanDeltaL"], places=9)
        # Every sampled pixel moved, so the mean ΔL* is the whole per-pixel ΔL*.
        expected = frame_delta.to_lab(200, 200, 200)[0] - frame_delta.to_lab(40, 40, 40)[0]
        self.assertAlmostEqual(brighter["MeanDeltaL"], expected, places=6)

    def test_mean_delta_l_is_diluted_by_pixels_that_did_not_move(self):
        # Unchanged pixels contribute exactly zero, which is why accumulating the sum over only the
        # changed ones is the same number and not an approximation.
        def half(x, y):
            return (200, 200, 200) if x == 0 else (40, 40, 40)

        stats = frame_delta.compare_buffers(
            buffer_of(2, 1, (40, 40, 40)), buffer_from(2, 1, half), 2, 1, stride=1)
        full = frame_delta.to_lab(200, 200, 200)[0] - frame_delta.to_lab(40, 40, 40)[0]
        self.assertAlmostEqual(stats["MeanDeltaL"], full / 2, places=6)


class RegionRestriction(unittest.TestCase):
    """A rect measures the rect and nothing else — the property the whole option is for."""

    def setUp(self):
        # Left half changes wildly, right half is untouched.
        self.before = buffer_of(4, 2, (0, 0, 0))

        def after(x, y):
            return (255, 255, 255) if x < 2 else (0, 0, 0)

        self.after = buffer_from(4, 2, after)

    def test_whole_frame_sees_both_halves(self):
        stats = frame_delta.compare_buffers(self.before, self.after, 4, 2, stride=1)
        self.assertEqual(stats["ChangedFraction"], 0.5)
        self.assertEqual(stats["SampledPixels"], 8)

    def test_region_over_the_changed_half_sees_only_change(self):
        stats = frame_delta.compare_buffers(
            self.before, self.after, 4, 2, frame_delta.Region(0, 0, 2, 2), stride=1)
        self.assertEqual(stats["ChangedFraction"], 1.0)
        self.assertEqual(stats["SampledPixels"], 4)
        self.assertAlmostEqual(stats["MedianDeltaE"], 100.0, places=3)

    def test_region_over_the_untouched_half_sees_nothing(self):
        stats = frame_delta.compare_buffers(
            self.before, self.after, 4, 2, frame_delta.Region(2, 0, 2, 2), stride=1)
        self.assertEqual(stats["ChangedFraction"], 0.0)
        self.assertEqual(stats["MedianDeltaE"], 0.0)

    def test_the_region_travels_with_the_result(self):
        stats = frame_delta.compare_buffers(
            self.before, self.after, 4, 2, frame_delta.Region(2, 0, 2, 2), stride=1)
        self.assertEqual(stats["Region"], {"X": 2, "Y": 0, "Width": 2, "Height": 2})


class Stride(unittest.TestCase):
    def test_stride_subsamples_rather_than_averaging(self):
        # Stride 2 over a 4x4 frame takes columns/rows 0 and 2 — four pixels. If it downscaled by
        # averaging instead, a checkerboard change would smear into a uniform small difference; here
        # it stays a full-magnitude change on the pixels it lands on.
        def checker(x, y):
            return (255, 255, 255) if (x + y) % 2 == 0 else (0, 0, 0)

        stats = frame_delta.compare_buffers(
            buffer_of(4, 4, (0, 0, 0)), buffer_from(4, 4, checker), 4, 4, stride=2)

        self.assertEqual(stats["SampledPixels"], 4)
        self.assertEqual(stats["ChangedFraction"], 1.0)
        self.assertAlmostEqual(stats["MedianDeltaE"], 100.0, places=3)
        self.assertEqual(stats["Stride"], 2)

    def test_stride_below_one_is_refused(self):
        frame = buffer_of(2, 2, (0, 0, 0))
        with self.assertRaises(ValueError):
            frame_delta.compare_buffers(frame, frame, 2, 2, stride=0)


class Hue(unittest.TestCase):
    """Duv's SIGN, which is the claim a magnitude alone cannot make."""

    def test_magenta_is_below_the_locus_and_green_is_above(self):
        # Not pinned numbers: the assertion is which SIDE each hue lands on, which is a property of
        # the Planckian locus rather than of this implementation. A ΔE that stayed on the locus is a
        # warmth change; only crossing into negative Duv is a purple one.
        self.assertLess(frame_delta.duv(255, 180, 255)[0], 0.0)
        self.assertGreater(frame_delta.duv(180, 255, 180)[0], 0.0)

    def test_black_has_no_chromaticity_and_says_so_rather_than_dividing_by_zero(self):
        self.assertEqual(frame_delta.duv(0, 0, 0), (0.0, 0.0))

    def test_warmer_light_has_a_lower_cct(self):
        # The single easiest thing to get backwards in this whole module, and the reason
        # delta_gate's "warmer" direction subtracts in the order it does.
        orange = frame_delta.duv(255, 170, 90)[1]
        blue = frame_delta.duv(180, 210, 255)[1]
        self.assertLess(orange, blue)

    def test_hue_travels_with_the_result(self):
        stats = frame_delta.compare_buffers(
            buffer_of(2, 2, (200, 200, 200)), buffer_of(2, 2, (220, 180, 220)), 2, 2, stride=1)
        self.assertLess(stats["DuvAfter"], stats["DuvBefore"])
        self.assertGreater(stats["CctBefore"], 0.0)


class BufferValidation(unittest.TestCase):
    def test_wrong_sized_buffer_is_refused(self):
        with self.assertRaises(ValueError):
            frame_delta.compare_buffers(
                buffer_of(4, 4, (0, 0, 0)), buffer_of(2, 2, (0, 0, 0)), 4, 4)

    def test_a_region_and_stride_that_select_nothing_is_refused(self):
        # Cannot happen through parse_region, which rejects a zero-sized rect — but compare_buffers
        # is reachable directly and a "median over no pixels" must not be invented.
        frame = buffer_of(4, 4, (0, 0, 0))
        with self.assertRaises(ValueError):
            frame_delta.compare_buffers(frame, frame, 4, 4, frame_delta.Region(0, 0, 0, 0), stride=1)


class Formatting(unittest.TestCase):
    def test_human_report_names_every_headline_number(self):
        stats = frame_delta.compare_buffers(
            buffer_of(4, 4, (0, 0, 0)), buffer_of(4, 4, (255, 255, 255)), 4, 4, stride=1)
        text = frame_delta.format_report(stats)
        for expected in ("median dE", "p99 dE", "changed", "mean L*", "Duv", "obvious"):
            self.assertIn(expected, text)

    def test_stats_are_json_serializable(self):
        # The gate writes these straight into the scenario report, which is JSON. A stray tuple or a
        # NaN here would surface as a crash three steps downstream of the code that produced it.
        import json
        stats = frame_delta.compare_buffers(
            buffer_of(4, 4, (1, 2, 3)), buffer_of(4, 4, (4, 5, 6)), 4, 4, stride=1)
        round_tripped = json.loads(json.dumps(stats))
        self.assertEqual(round_tripped["SampledPixels"], 16)
        self.assertFalse(any(isinstance(v, float) and math.isnan(v)
                             for v in round_tripped.values()))


if __name__ == "__main__":
    unittest.main()
