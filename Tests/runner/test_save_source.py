#!/usr/bin/env python3
"""Offline tests for run_test.sh's save-source resolution — fixture, or -quicktest, or refuse.

The branch is three lines of bash and it decides whether a run measures the world the scenario asked
for. Getting it wrong is not loud: before this was a refusal, a scenario naming a fixture that wasn't
there fell through to -quicktest with one log line, and the run went green against a randomly
generated colony. The symptom arrives much later and looks like something else entirely — two arms of
an A/B disagreeing about 92% of their pixels, a whole in-game day apart, one of them under a
"permanent summer" condition the other never had.

The cause is mundane and will recur: Fixtures/*.rws is gitignored, so every `git worktree add`
produces a tree with no fixtures, and this repo's own contributing guide tells people to work in
worktrees.

Drives the SHIPPED function rather than a Python restatement, like test_overlay_activation.py: the
definition is lifted verbatim out of Runner/run_test.sh and sourced into a stub that supplies log
and fail.

Run: python3 -m unittest discover -s Tests/runner   (or via ./test.sh)
"""

import os
import subprocess
import tempfile
import unittest

RUN_TEST_SH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "Runner", "run_test.sh"
)

FIRST_LINE = "resolve_save_source() {"
LAST_LINE = "resolve_save_source"


def save_source_function():
    with open(RUN_TEST_SH, encoding="utf-8") as handle:
        lines = handle.read().split("\n")
    start = lines.index(FIRST_LINE)
    # The bare invocation at column 0, not the definition line above it.
    end = lines.index(LAST_LINE, start)
    return "\n".join(lines[start:end])


STUB = """
set -uo pipefail
log() { echo "[log] $*"; }
fail() { echo "[fail] $*" >&2; exit 3; }
"""


class SaveSourceTests(unittest.TestCase):
    def run_resolve(self, save_file, fixture_names=()):
        with tempfile.TemporaryDirectory() as repo:
            os.makedirs(os.path.join(repo, "Fixtures"))
            for name in fixture_names:
                with open(os.path.join(repo, "Fixtures", name), "w") as handle:
                    handle.write("not really a save")

            script = "\n".join([
                STUB,
                f'REPO_DIR="{repo}"',
                f'SAVE_FILE="{save_file}"',
                save_source_function(),
                "resolve_save_source",
                'echo "USE_QUICKTEST=$USE_QUICKTEST"',
            ])
            return subprocess.run(["bash", "-c", script], capture_output=True, text=True)

    def test_named_fixture_that_exists_is_used(self):
        result = self.run_resolve("minimal_colony.rws", ["minimal_colony.rws"])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("USE_QUICKTEST=0", result.stdout)
        self.assertIn("loaded via vanilla autostart", result.stdout)

    def test_no_save_file_falls_back_to_quicktest(self):
        # Still a legitimate, documented mode: a scenario whose probes depend only on scripted state
        # (latitude, season, time) does not need a colony anyone authored.
        result = self.run_resolve("")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("USE_QUICKTEST=1", result.stdout)

    def test_named_fixture_that_is_missing_refuses_to_run(self):
        # The whole point. Degrading to a random world here is the "green run that means less than it
        # looks like" this repo refuses on principle.
        result = self.run_resolve("minimal_colony.rws")
        self.assertEqual(result.returncode, 3)
        self.assertIn("no fixture at", result.stderr)
        self.assertNotIn("USE_QUICKTEST", result.stdout)

    def test_missing_fixture_message_names_the_worktree_cause(self):
        # A bare "not found" would send someone looking for a corrupt save. The overwhelmingly likely
        # cause is a gitignored fixture in a fresh worktree, so the message says so and gives the
        # symlink that fixes it.
        result = self.run_resolve("minimal_colony.rws")
        self.assertIn("gitignored", result.stderr)
        self.assertIn("worktree", result.stderr)
        self.assertIn("ln -s", result.stderr)

    def test_a_different_fixture_being_present_does_not_satisfy_the_request(self):
        result = self.run_resolve("minimal_colony.rws", ["found_a_seam.rws"])
        self.assertEqual(result.returncode, 3)


if __name__ == "__main__":
    unittest.main()
