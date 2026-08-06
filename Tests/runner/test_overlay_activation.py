#!/usr/bin/env python3
"""Offline tests for run_test.sh's Step 3b — "every --mod-overlay target must actually be activated".

Step 3b is bash, not C#, so it has no home in Shared/. It is still the part of the overlay path with
branching worth getting wrong (whose <li> elements count, what case they are in, whether a substring
match is good enough), and getting it wrong in either direction is expensive: a false negative brings
back the misleading build-skew diagnosis of issue #27, and a false positive refuses a run that would
have been fine, which is the kind of thing people work around by deleting the check.

So these tests drive the SHIPPED bash functions rather than a Python restatement of them: the two
function definitions are lifted verbatim out of Runner/run_test.sh and sourced into a stub harness
that supplies log/fail and the MOD_OVERLAY_* arrays. Same reasoning as linking a source file into a
test project instead of copying it — a copy would pass forever after the original changed.

Run: python3 -m unittest discover -s Tests/runner   (or via ./test.sh)
"""

import os
import subprocess
import tempfile
import unittest

RUN_TEST_SH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "Runner", "run_test.sh"
)

# The two functions Step 3b is made of. Sliced by their own definition line and by the bare call that
# follows them, because those are the lines that cannot move without someone noticing — a marker
# comment could be edited away while the code kept working, and then this file would test nothing.
FIRST_LINE = "read_active_mod_ids() {"
LAST_LINE = "assert_overlay_targets_activated"


def step3b_source():
    with open(RUN_TEST_SH, encoding="utf-8") as handle:
        lines = handle.read().split("\n")
    start = lines.index(FIRST_LINE)
    # The bare invocation, at column 0 — not the indented references inside the definitions.
    end = lines.index(LAST_LINE, start)
    body = "\n".join(lines[start:end])
    if "read_active_mod_ids()" not in body or "assert_overlay_targets_activated()" not in body:
        raise AssertionError(
            f"could not lift Step 3b out of {RUN_TEST_SH} — the extraction markers have moved"
        )
    return body


# set -euo pipefail because that is the context the real script runs in, and the house comment above
# matches_package_id exists because `set -e` changes what a loop body's last command means. A test
# running under looser settings would not catch that class of regression.
HARNESS = """
set -euo pipefail
log()  {{ echo "[run_test] $*"; }}
fail() {{ echo "[run_test] FAIL: $*" >&2; exit 1; }}

MODSCONFIG="$1"; shift

# The three arrays the real resolution loop fills, kept in step by index. Only the ids drive the
# check; the other two exist so the failure message can name a path, so they are derived from the id.
MOD_OVERLAY_IDS=()
MOD_OVERLAY_ARGS=()
MOD_OVERLAY_TARGETS=()
for overlay_id in "$@"; do
    MOD_OVERLAY_IDS+=("$overlay_id")
    MOD_OVERLAY_ARGS+=("/worktrees/$overlay_id")
    MOD_OVERLAY_TARGETS+=("/Mods/$overlay_id")
done

{step3b}

assert_overlay_targets_activated
"""


def mods_config(active, known=("ludeon.rimworld.royalty", "ludeon.rimworld.odyssey")):
    """A ModsConfig.xml in exactly the shape Step 3 writes one."""
    active_lis = "\n".join(f"    <li>{pid}</li>" for pid in active)
    known_lis = "\n".join(f"    <li>{pid}</li>" for pid in known)
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<ModsConfigData>\n"
        "  <version>1.6.4566 rev435</version>\n"
        f"  <activeMods>\n{active_lis}\n  </activeMods>\n"
        f"  <knownExpansions>\n{known_lis}\n  </knownExpansions>\n"
        "</ModsConfigData>\n"
    )


class Step3bTestCase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.script = HARNESS.format(step3b=step3b_source())

    def check(self, modsconfig_text, overlay_ids):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "ModsConfig.xml")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(modsconfig_text)
            done = subprocess.run(
                ["bash", "-c", self.script, "_", path, *overlay_ids],
                capture_output=True,
                text=True,
            )
        return done.returncode, done.stdout + done.stderr


class Accepts(Step3bTestCase):
    def test_overlay_target_listed_in_active_mods_passes(self):
        code, out = self.check(
            mods_config(["ludeon.rimworld", "joof.celestiallighting", "joof.rimworldtestharness"]),
            ["joof.celestiallighting"],
        )
        self.assertEqual(code, 0, out)
        self.assertIn("all 1 overlay target(s) are active", out)

    def test_required_mods_casing_is_folded_before_comparing(self):
        # A scenario's requiredMods keys reach ModsConfig.xml verbatim, so a mod activated that way
        # can be listed in any case at all. It is still activated, and refusing the run would be the
        # false positive that makes people stop trusting the check.
        code, out = self.check(
            mods_config(["ludeon.rimworld", "Joof.CelestialLighting"]),
            ["joof.celestiallighting"],
        )
        self.assertEqual(code, 0, out)
        self.assertIn("all 1 overlay target(s) are active", out)

    def test_no_overlays_at_all_says_nothing(self):
        # Every run that passes no --mod-overlay goes through here. It must be silent, not a line of
        # noise about zero targets.
        code, out = self.check(mods_config(["ludeon.rimworld"]), [])
        self.assertEqual(code, 0, out)
        self.assertNotIn("Step 3b", out)

    def test_several_overlays_all_activated_pass_together(self):
        code, out = self.check(
            mods_config(["ludeon.rimworld", "joof.celestiallighting", "joof.performancesearch"]),
            ["joof.celestiallighting", "joof.performancesearch"],
        )
        self.assertEqual(code, 0, out)
        self.assertIn("all 2 overlay target(s) are active", out)


class Refuses(Step3bTestCase):
    def test_unactivated_overlay_target_fails_and_names_the_flag_to_add(self):
        # The whole point of issue #27: this used to run to completion and blame build skew.
        code, out = self.check(
            mods_config(["ludeon.rimworld", "joof.rimworldtestharness"]),
            ["joof.celestiallighting"],
        )
        self.assertEqual(code, 1, out)
        self.assertIn("which is not activated for this run", out)
        self.assertIn("joof.celestiallighting", out)
        self.assertIn("--mod /Mods/joof.celestiallighting", out)
        self.assertIn("requiredMods", out)

    def test_known_expansions_do_not_count_as_activation(self):
        # <knownExpansions> is carried over verbatim from the user's real file and has <li> entries of
        # its own — including, after --without-dlc, ones this run deliberately left inactive. Reading
        # the whole file instead of just <activeMods> would silently accept those.
        code, out = self.check(
            mods_config(["ludeon.rimworld"], known=["joof.celestiallighting"]),
            ["joof.celestiallighting"],
        )
        self.assertEqual(code, 1, out)
        self.assertIn("which is not activated for this run", out)

    def test_a_longer_id_containing_the_target_is_not_a_match(self):
        # Substring matching would let an add-on's packageId vouch for the mod it extends.
        code, out = self.check(
            mods_config(["ludeon.rimworld", "joof.celestiallighting.patches"]),
            ["joof.celestiallighting"],
        )
        self.assertEqual(code, 1, out)
        self.assertIn("which is not activated for this run", out)

    def test_second_of_several_overlays_is_reported_by_name(self):
        code, out = self.check(
            mods_config(["ludeon.rimworld", "joof.celestiallighting"]),
            ["joof.celestiallighting", "joof.performancesearch"],
        )
        self.assertEqual(code, 1, out)
        self.assertIn("joof.performancesearch, which is not activated", out)

    def test_modsconfig_without_active_mods_is_an_error_not_a_pass(self):
        # A parse that quietly returned nothing would turn every overlay into a failure; a parse that
        # quietly returned success would turn the check off. Neither is acceptable, so the read
        # failing is its own named error.
        code, out = self.check("<ModsConfigData></ModsConfigData>\n", ["joof.celestiallighting"])
        self.assertEqual(code, 1, out)
        self.assertIn("could not read the activeMods list", out)


if __name__ == "__main__":
    unittest.main()
