#!/usr/bin/env python3
"""Offline tests for Runner/compose_ab.py.

No ffmpeg and no frames: what is tested is the part that decides WHAT gets burnt in and which files
get paired — sequence discovery, the difference banner, caption sourcing, and the escaping that
keeps a caption from silently truncating inside a filtergraph. The encoding itself is ffmpeg's
business and is not re-tested here.

Run: python3 -m unittest discover -s Tests/runner   (or via ./test.sh)
"""

import argparse
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Runner"))

import compose_ab  # noqa: E402


def args_for(**overrides):
    values = {"caption": "frame {index}", "caption_from": 0.0, "caption_step": 1.0}
    values.update(overrides)
    return argparse.Namespace(**values)


class DrawtextEscaping(unittest.TestCase):
    """A caption is user text now, and drawtext eats three characters without complaining."""

    def test_plain_text_is_untouched(self):
        self.assertEqual(compose_ab.escape_drawtext("hour 20.05"), "hour 20.05")

    def test_a_colon_is_escaped(self):
        # ':' separates a filter's options. Unescaped, "elevation: -3.2" ends the text argument and
        # the caption silently loses everything after it — no error, just a truncated picture.
        self.assertEqual(compose_ab.escape_drawtext("elevation: -3.2"), "elevation\\: -3.2")

    def test_a_quote_is_escaped(self):
        self.assertEqual(compose_ab.escape_drawtext("Dub's mod"), "Dub\\'s mod")

    def test_backslashes_go_first(self):
        # Order matters: escape the backslashes after the colons and the escape we just added gets
        # escaped in turn, which puts a literal "\:" on screen instead of a colon.
        self.assertEqual(compose_ab.escape_drawtext("a\\b:c"), "a\\\\b\\:c")

    def test_a_percent_is_left_alone(self):
        # Handled by expansion=none in build_filter rather than by escaping every caption that
        # mentions a percentage.
        self.assertEqual(compose_ab.escape_drawtext("50% dimmer"), "50% dimmer")


class FilterConstruction(unittest.TestCase):
    def test_captions_reach_the_filtergraph_escaped(self):
        graph = compose_ab.build_filter(
            "/font.ttf", "OFF", "ON", "hour 20.05: dusk", "frames differ", "0xE8A0FF")
        self.assertIn("hour 20.05\\: dusk", graph)
        self.assertNotIn("hour 20.05: dusk", graph)

    def test_expansion_is_disabled_on_every_caption(self):
        graph = compose_ab.build_filter("/font.ttf", "OFF", "ON", "info", "status", "white")
        self.assertEqual(graph.count("expansion=none"), 4)

    def test_the_pair_is_stacked_and_divided(self):
        graph = compose_ab.build_filter("/font.ttf", "OFF", "ON", "info", "status", "white")
        self.assertIn("hstack=inputs=2", graph)
        self.assertIn("drawbox", graph)


class SequenceDiscovery(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="compose_ab-test-")
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)

    def write(self, name, content=b"frame"):
        path = os.path.join(self.tmp, name)
        with open(path, "wb") as handle:
            handle.write(content)
        return path

    def test_frames_are_found_in_expander_order(self):
        for i in range(3):
            self.write(f"lapse_{i:04d}.png")
        found = compose_ab.frame_paths(self.tmp, "lapse")
        self.assertEqual([os.path.basename(p) for p in found],
                         ["lapse_0000.png", "lapse_0001.png", "lapse_0002.png"])

    def test_discovery_stops_at_the_first_gap(self):
        # Same rule as run_test.sh's glob and Shared/TimelapseExpander's numbering. A gap means an
        # interrupted sweep, and pairing across it would compare frames from different moments.
        self.write("lapse_0000.png")
        self.write("lapse_0002.png")
        self.assertEqual(len(compose_ab.frame_paths(self.tmp, "lapse")), 1)

    def test_an_unknown_prefix_finds_nothing(self):
        self.write("lapse_0000.png")
        self.assertEqual(compose_ab.frame_paths(self.tmp, "other"), [])

    def test_a_scenario_qualified_prefix_is_just_a_prefix(self):
        # Suite mode prefixes every screenshot with its scenario (Shared/SuiteScreenshots); nothing
        # here needs to know that, which is the point.
        self.write("MyScenario__lapse_0000.png")
        self.assertEqual(len(compose_ab.frame_paths(self.tmp, "MyScenario__lapse")), 1)

    def test_identical_frames_are_recognised_as_identical(self):
        a = self.write("a.png", b"same bytes")
        b = self.write("b.png", b"same bytes")
        self.assertFalse(compose_ab.frames_differ(a, b))

    def test_differing_frames_are_recognised_as_differing(self):
        a = self.write("a.png", b"one")
        b = self.write("b.png", b"two")
        self.assertTrue(compose_ab.frames_differ(a, b))


class Captions(unittest.TestCase):
    def test_the_default_names_the_frame_index(self):
        self.assertEqual(compose_ab.caption_for(7, args_for(), None), "frame 7")

    def test_a_linear_ramp_matches_a_timelapse_sweep(self):
        # fromHour 20.0, stepHours 0.05: frame 3 is hour 20.15, which is exactly what the Timelapse
        # expander set the clock to. The tool derives nothing — it re-states what the caller declared.
        args = args_for(caption="hour {value:.3f}", caption_from=20.0, caption_step=0.05)
        self.assertEqual(compose_ab.caption_for(3, args, None), "hour 20.150")

    def test_index_and_value_are_both_available(self):
        args = args_for(caption="#{index} at {value:g}", caption_from=100.0, caption_step=10.0)
        self.assertEqual(compose_ab.caption_for(2, args, None), "#2 at 120")

    def test_a_captions_file_wins_outright(self):
        # How a mod burns in something it derived itself — a fitted sun elevation, a probe reading —
        # without this tool having to know what that quantity is.
        lines = ["sun -3.21°", "sun -4.05°", "sun -4.89°"]
        self.assertEqual(compose_ab.caption_for(1, args_for(), lines), "sun -4.05°")

    def test_a_short_captions_file_yields_blanks_rather_than_crashing(self):
        # Losing the caption on the tail of a sweep is a worse video; losing the video is worse still.
        self.assertEqual(compose_ab.caption_for(5, args_for(), ["only one"]), "")

    def test_no_captions_file_means_none(self):
        self.assertIsNone(compose_ab.load_captions(None))


class CaptionFileLoading(unittest.TestCase):
    def test_lines_are_read_without_their_newlines(self):
        tmp = tempfile.mkdtemp(prefix="compose_ab-test-")
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        path = os.path.join(tmp, "captions.txt")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write("first\nsecond\n")
        self.assertEqual(compose_ab.load_captions(path), ["first", "second"])


if __name__ == "__main__":
    unittest.main()
