#!/usr/bin/env python3
"""Offline tests for Runner/asset_claims.py.

No game, no lock, no filesystem outside a tmpdir. Every case here is one of the live failures that
motivated the ledger, written down as something that can fail in a second instead of in a three-hour
run: a stale Mods/TestMod winning over the folder the run was given, a teardown deleting a save it
never backed up, a recovery overwriting a file someone had edited since, and the config swap that
left an 828-mod list in a scratch directory while every later run backed up the test list.

Run: python3 -m unittest discover -s Tests/runner   (or via ./test.sh)
"""

import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Runner"))

import asset_claims  # noqa: E402


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
    return path


def read(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read()


class ClaimTestCase(unittest.TestCase):
    """Shared fixture: a ledger plus a scratch tree standing in for the machine's global state."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.ledger = os.path.join(self.tmp, "claims.json")
        self.backups = os.path.join(self.tmp, "backups")
        asset_claims.main(["--ledger", self.ledger, "begin",
                           "--run-id", "test-run", "--backup-dir", self.backups])

    def path(self, *parts):
        return os.path.join(self.tmp, *parts)

    def claims(self):
        with open(self.ledger, encoding="utf-8") as handle:
            return json.load(handle)["claims"]

    def restore(self, guard=False):
        return asset_claims.restore(self.ledger, guard=guard)

    def actions(self, results):
        return [r["action"] for r in results]


class FileClaims(ClaimTestCase):
    def test_install_over_existing_file_restores_original_content(self):
        dest = write(self.path("mod", "1.6", "Assemblies", "X.dll"), "MAIN BUILD")
        src = write(self.path("worktree", "1.6", "Assemblies", "X.dll"), "BRANCH BUILD")

        asset_claims.claim_path(self.ledger, dest=dest, src=src)
        self.assertEqual(read(dest), "BRANCH BUILD")

        self.restore()
        self.assertEqual(read(dest), "MAIN BUILD")

    def test_install_where_nothing_existed_is_removed_again(self):
        os.makedirs(self.path("mod"))
        dest = self.path("mod", "New.dll")
        src = write(self.path("src", "New.dll"), "new")

        asset_claims.claim_path(self.ledger, dest=dest, src=src)
        self.assertTrue(os.path.exists(dest))

        self.restore()
        self.assertFalse(os.path.exists(dest),
                         "a file that did not exist before the run must not survive it")

    def test_absent_prior_is_recorded_not_merely_missing(self):
        """The distinction that stops teardown deleting a save it never backed up.

        The old script inferred "no prior file existed" from "no backup file was made", so a run that
        failed before backing up reached teardown and removed the user's real autostart.rws.
        """
        os.makedirs(self.path("saves"))
        dest = self.path("saves", "autostart.rws")
        asset_claims.claim_path(self.ledger, dest=dest)
        self.assertEqual(self.claims()[0]["prior"], asset_claims.PRIOR_ABSENT)

        real_save = write(dest, "a save someone made later")
        results = self.restore()
        self.assertEqual(self.actions(results), ["removed"])
        self.assertFalse(os.path.exists(real_save))

    def test_claim_then_seal_records_what_the_caller_wrote(self):
        """ModsConfig.xml's shape: the runner generates it, so the hash can only be taken afterwards."""
        dest = write(self.path("Config", "ModsConfig.xml"), "<real>828 mods</real>")
        asset_claims.claim_path(self.ledger, dest=dest)
        self.assertIsNone(self.claims()[0]["installed_sha256"])

        write(dest, "<test>14 mods</test>")
        asset_claims.seal(self.ledger, dest)
        self.assertEqual(self.claims()[0]["installed_sha256"], asset_claims.sha256_of(dest))

        self.restore()
        self.assertEqual(read(dest), "<real>828 mods</real>")

    def test_seal_of_unclaimed_path_is_an_error(self):
        with self.assertRaises(asset_claims.ClaimError):
            asset_claims.seal(self.ledger, self.path("never-claimed"))

    def test_claiming_a_directory_is_refused(self):
        os.makedirs(self.path("a-dir"))
        with self.assertRaises(asset_claims.ClaimError):
            asset_claims.claim_path(self.ledger, dest=self.path("a-dir"))

    def test_install_onto_a_symlink_does_not_write_through_it(self):
        """Writing through the link would modify a file the run never claimed and cannot restore."""
        target = write(self.path("elsewhere", "real.dll"), "SOMEONE ELSES FILE")
        link = self.path("mod", "X.dll")
        os.makedirs(self.path("mod"))
        os.symlink(target, link)
        src = write(self.path("src", "X.dll"), "OURS")

        asset_claims.claim_path(self.ledger, dest=link, src=src)
        self.assertEqual(read(target), "SOMEONE ELSES FILE")
        self.assertEqual(read(link), "OURS")
        self.assertFalse(os.path.islink(link))

        self.restore()
        self.assertTrue(os.path.islink(link), "the link itself must come back, not a copy of it")
        self.assertEqual(os.readlink(link), target)

    def test_missing_backup_is_reported_not_raised(self):
        dest = write(self.path("mod", "X.dll"), "MAIN")
        src = write(self.path("src", "X.dll"), "BRANCH")
        asset_claims.claim_path(self.ledger, dest=dest, src=src)
        os.unlink(self.claims()[0]["backup"])

        results = self.restore()
        self.assertEqual(self.actions(results), ["failed"])
        self.assertIn("backup missing", results[0]["detail"])


class OverlayClaims(ClaimTestCase):
    def setUp(self):
        super().setUp()
        self.dest_dir = self.path("main", "1.6", "Assemblies")
        self.src_dir = self.path("worktree", "1.6", "Assemblies")
        write(os.path.join(self.dest_dir, "Mod.dll"), "MAIN DLL")
        write(os.path.join(self.dest_dir, "Mod.pdb"), "MAIN PDB")
        write(os.path.join(self.dest_dir, "Untouched.txt"), "not part of the build")

    def test_overlay_installs_every_file_and_restores_exactly(self):
        write(os.path.join(self.src_dir, "Mod.dll"), "BRANCH DLL")
        write(os.path.join(self.src_dir, "Mod.pdb"), "BRANCH PDB")
        write(os.path.join(self.src_dir, "Extra.dll"), "BRANCH EXTRA")

        asset_claims.claim_overlay(self.ledger, self.src_dir, self.dest_dir)
        self.assertEqual(read(os.path.join(self.dest_dir, "Mod.dll")), "BRANCH DLL")
        self.assertEqual(read(os.path.join(self.dest_dir, "Mod.pdb")), "BRANCH PDB")
        self.assertEqual(read(os.path.join(self.dest_dir, "Extra.dll")), "BRANCH EXTRA")

        self.restore()
        self.assertEqual(read(os.path.join(self.dest_dir, "Mod.dll")), "MAIN DLL")
        self.assertEqual(read(os.path.join(self.dest_dir, "Mod.pdb")), "MAIN PDB")
        self.assertFalse(os.path.exists(os.path.join(self.dest_dir, "Extra.dll")),
                         "a file the overlay added must not survive the run")

    def test_dll_and_pdb_move_together(self):
        """The stale-.pdb segfault, made structurally impossible.

        Copying only the .dll leaves a symbol file that doesn't match it; Mono faults during assembly
        load and RimWorld dies with signo:11 right after the harness loads, which reads as a crash in
        the mod's own code. A directory overlay cannot produce a mismatched pair.
        """
        write(os.path.join(self.src_dir, "Mod.dll"), "BRANCH DLL")
        write(os.path.join(self.src_dir, "Mod.pdb"), "BRANCH PDB")

        asset_claims.claim_overlay(self.ledger, self.src_dir, self.dest_dir)
        installed = {os.path.basename(c["path"]) for c in self.claims()}
        self.assertEqual(installed, {"Mod.dll", "Mod.pdb"})

    def test_overlay_leaves_unrelated_destination_files_alone(self):
        write(os.path.join(self.src_dir, "Mod.dll"), "BRANCH DLL")
        asset_claims.claim_overlay(self.ledger, self.src_dir, self.dest_dir)
        self.assertEqual(read(os.path.join(self.dest_dir, "Untouched.txt")), "not part of the build")

    def test_overlay_recurses(self):
        write(os.path.join(self.src_dir, "sub", "Nested.dll"), "NESTED")
        asset_claims.claim_overlay(self.ledger, self.src_dir, self.dest_dir)
        self.assertEqual(read(os.path.join(self.dest_dir, "sub", "Nested.dll")), "NESTED")
        self.restore()
        self.assertFalse(os.path.exists(os.path.join(self.dest_dir, "sub", "Nested.dll")))

    def test_empty_source_is_refused(self):
        """An unbuilt worktree must fail loudly, not install nothing and run against the main build."""
        os.makedirs(self.src_dir)
        with self.assertRaises(asset_claims.ClaimError) as caught:
            asset_claims.claim_overlay(self.ledger, self.src_dir, self.dest_dir)
        self.assertIn("no files", str(caught.exception))

    def test_missing_destination_is_refused(self):
        write(os.path.join(self.src_dir, "Mod.dll"), "BRANCH")
        with self.assertRaises(asset_claims.ClaimError):
            asset_claims.claim_overlay(self.ledger, self.src_dir, self.path("does", "not", "exist"))


class SymlinkClaims(ClaimTestCase):
    def setUp(self):
        super().setUp()
        self.mods = self.path("Mods")
        os.makedirs(self.mods)
        self.mine = self.path("worktrees", "mine", "TestMod")
        self.theirs = self.path("worktrees", "theirs", "TestMod")
        os.makedirs(self.mine)
        os.makedirs(self.theirs)

    def link(self):
        return os.path.join(self.mods, "TestMod")

    def test_creates_and_removes_a_link_that_was_not_there(self):
        asset_claims.claim_symlink(self.ledger, self.link(), self.mine)
        self.assertEqual(os.readlink(self.link()), self.mine)

        self.restore()
        self.assertFalse(os.path.lexists(self.link()))

    def test_stale_link_from_another_branch_is_repointed_then_put_back(self):
        """The failure this flag exists for.

        Every worktree's probe mod has the same basename and packageId, so a link left behind by a
        crashed run on another branch used to win over the --mod this run was given: the game loaded
        that branch's probes, and the tell was only the *newest* probes going missing.
        """
        os.symlink(self.theirs, self.link())

        claim = asset_claims.claim_symlink(self.ledger, self.link(), self.mine)
        self.assertEqual(os.readlink(self.link()), self.mine)
        self.assertEqual(claim["prior_target"], self.theirs)

        self.restore()
        self.assertEqual(os.readlink(self.link()), self.theirs,
                         "the other branch's link is theirs to keep — we only borrowed the name")

    def test_already_correct_link_round_trips_unchanged(self):
        os.symlink(self.mine, self.link())
        asset_claims.claim_symlink(self.ledger, self.link(), self.mine)
        self.restore()
        self.assertEqual(os.readlink(self.link()), self.mine)

    def test_real_directory_is_refused(self):
        """A genuinely installed mod: loading it instead of the named folder is the exact bug we end."""
        os.makedirs(os.path.join(self.mods, "RealMod"))
        with self.assertRaises(asset_claims.ClaimError) as caught:
            asset_claims.claim_symlink(self.ledger, os.path.join(self.mods, "RealMod"), self.mine)
        self.assertIn("real directory", str(caught.exception))

    def test_regular_file_in_the_way_is_refused(self):
        write(os.path.join(self.mods, "NotAMod"), "x")
        with self.assertRaises(asset_claims.ClaimError):
            asset_claims.claim_symlink(self.ledger, os.path.join(self.mods, "NotAMod"), self.mine)


class GuardedRecovery(ClaimTestCase):
    """The rules for rolling back a ledger a *dead* run left behind.

    Restoring a backup that may be hours old must not clobber anything changed since, so a claim is
    only undone if what is on disk is still byte-for-byte what that run installed.
    """

    def test_untouched_file_is_restored(self):
        dest = write(self.path("Config", "ModsConfig.xml"), "REAL 828 MODS")
        src = write(self.path("src", "ModsConfig.xml"), "TEST 14 MODS")
        asset_claims.claim_path(self.ledger, dest=dest, src=src)

        results = self.restore(guard=True)
        self.assertEqual(self.actions(results), ["restored"])
        self.assertEqual(read(dest), "REAL 828 MODS")

    def test_file_edited_since_install_is_left_alone(self):
        dest = write(self.path("Config", "ModsConfig.xml"), "REAL 828 MODS")
        src = write(self.path("src", "ModsConfig.xml"), "TEST 14 MODS")
        asset_claims.claim_path(self.ledger, dest=dest, src=src)
        write(dest, "SOMEONE CURATED THIS AFTERWARDS")

        results = self.restore(guard=True)
        self.assertEqual(self.actions(results), ["skipped"])
        self.assertEqual(read(dest), "SOMEONE CURATED THIS AFTERWARDS",
                         "a wrong automatic restore is worse than a loud manual one")

    def test_unsealed_claim_cannot_be_proven_untouched(self):
        """No recorded hash means no evidence — which is not the same as permission."""
        dest = write(self.path("Config", "Prefs.xml"), "ORIGINAL")
        asset_claims.claim_path(self.ledger, dest=dest)   # never sealed
        write(dest, "MODIFIED BY THE RUN")

        results = self.restore(guard=True)
        self.assertEqual(self.actions(results), ["skipped"])
        self.assertIn("no installed hash", results[0]["detail"])

    def test_unguarded_restore_of_our_own_run_is_unconditional(self):
        """RimWorld rewrites Prefs.xml wholesale on exit, so our own teardown cannot be guarded."""
        dest = write(self.path("Config", "Prefs.xml"), "ORIGINAL")
        asset_claims.claim_path(self.ledger, dest=dest)
        write(dest, "REWRITTEN BY THE GAME ON EXIT")

        results = self.restore(guard=False)
        self.assertEqual(self.actions(results), ["restored"])
        self.assertEqual(read(dest), "ORIGINAL")

    def test_symlink_claimed_by_someone_else_since_is_left_alone(self):
        mods = self.path("Mods")
        os.makedirs(mods)
        mine, theirs, third = (self.path("a"), self.path("b"), self.path("c"))
        for d in (mine, theirs, third):
            os.makedirs(d)
        link = os.path.join(mods, "TestMod")
        os.symlink(theirs, link)
        asset_claims.claim_symlink(self.ledger, link, mine)

        os.unlink(link)
        os.symlink(third, link)   # a later run took the name

        results = self.restore(guard=True)
        self.assertEqual(self.actions(results), ["skipped"])
        self.assertEqual(os.readlink(link), third)


class RestoreOrdering(ClaimTestCase):
    def test_claims_are_undone_newest_first(self):
        """Two runs' worth of claims on one path unwind to the state before the first."""
        dest = write(self.path("f.dll"), "ORIGINAL")
        first = write(self.path("src1", "f.dll"), "FIRST")
        second = write(self.path("src2", "f.dll"), "SECOND")

        asset_claims.claim_path(self.ledger, dest=dest, src=first)
        asset_claims.claim_path(self.ledger, dest=dest, src=second)
        self.assertEqual(read(dest), "SECOND")

        self.restore()
        self.assertEqual(read(dest), "ORIGINAL")

    def test_backups_of_same_named_files_do_not_collide(self):
        """Two claims routinely share a basename (.../Assemblies/Mod.dll from two trees)."""
        a = write(self.path("one", "Mod.dll"), "ONE")
        b = write(self.path("two", "Mod.dll"), "TWO")
        src = write(self.path("src", "Mod.dll"), "NEW")

        asset_claims.claim_path(self.ledger, dest=a, src=src)
        asset_claims.claim_path(self.ledger, dest=b, src=src)
        backups = [c["backup"] for c in self.claims()]
        self.assertEqual(len(set(backups)), 2)

        self.restore()
        self.assertEqual(read(a), "ONE")
        self.assertEqual(read(b), "TWO")


class CrashedRunRecovery(ClaimTestCase):
    """The whole mechanism, end to end, in the sequence run_test.sh performs it.

    A run claims the config, the save, the mod symlinks and the branch build; then it dies without
    tearing down. The next run holds the lock, so the ledger it finds cannot belong to anything live,
    and rolls the machine back before taking any claims of its own.

    Pinned as one test because the failure it replaces was emergent rather than local: each step was
    individually fine, and the damage came from a later run backing up what an earlier one had left
    behind. Every assertion below is a state that was observed on the real machine.
    """

    def simulate_run_then_crash(self):
        self.config = write(self.path("Config", "ModsConfig.xml"), "<real>828 mods</real>")
        self.prefs = write(self.path("Config", "Prefs.xml"), "<devMode>False</devMode>")
        self.save = self.path("Saves", "autostart.rws")
        os.makedirs(self.path("Saves"))

        self.mods = self.path("Mods")
        os.makedirs(self.mods)
        self.main_checkout = self.path("checkouts", "Mod")
        self.other_branch = self.path("worktrees", "other", "TestMod")
        self.my_branch = self.path("worktrees", "mine", "TestMod")
        for d in (self.main_checkout, self.other_branch, self.my_branch):
            os.makedirs(d)
        os.symlink(self.main_checkout, os.path.join(self.mods, "Mod"))
        os.symlink(self.other_branch, os.path.join(self.mods, "TestMod"))

        self.assemblies = os.path.join(self.main_checkout, "1.6", "Assemblies")
        write(os.path.join(self.assemblies, "Mod.dll"), "MAIN DLL")
        write(os.path.join(self.assemblies, "Mod.pdb"), "MAIN PDB")
        branch_build = self.path("worktrees", "mine", "1.6", "Assemblies")
        write(os.path.join(branch_build, "Mod.dll"), "BRANCH DLL")
        write(os.path.join(branch_build, "Mod.pdb"), "BRANCH PDB")

        # ---- the sequence run_test.sh runs ----
        asset_claims.claim_symlink(self.ledger, os.path.join(self.mods, "Mod"), self.main_checkout)
        asset_claims.claim_symlink(self.ledger, os.path.join(self.mods, "TestMod"), self.my_branch)
        asset_claims.claim_path(self.ledger, dest=self.config)
        write(self.config, "<test>14 mods</test>")
        asset_claims.seal(self.ledger, self.config)
        asset_claims.claim_path(self.ledger, dest=self.prefs)
        write(self.prefs, "<devMode>True</devMode>")
        asset_claims.seal(self.ledger, self.prefs)
        asset_claims.claim_path(self.ledger, dest=self.save)
        write(self.save, "FIXTURE")
        asset_claims.seal(self.ledger, self.save)
        asset_claims.claim_overlay(self.ledger, branch_build, self.assemblies)
        # ...and now the game segfaults at mod load. No teardown runs.

    def test_next_run_rolls_the_machine_back(self):
        self.simulate_run_then_crash()

        # What the crashed run left behind — each line an observed real-world symptom.
        self.assertEqual(read(self.config), "<test>14 mods</test>")
        self.assertEqual(os.readlink(os.path.join(self.mods, "TestMod")), self.my_branch)
        self.assertEqual(read(os.path.join(self.assemblies, "Mod.dll")), "BRANCH DLL")

        results = asset_claims.restore(self.ledger, guard=True)
        self.assertEqual(set(self.actions(results)), {"restored", "removed"})

        self.assertEqual(read(self.config), "<real>828 mods</real>",
                         "the real mod list must come back before the next run can back up the test one")
        self.assertEqual(read(self.prefs), "<devMode>False</devMode>")
        self.assertFalse(os.path.exists(self.save), "the fixture save was never the user's")
        self.assertEqual(os.readlink(os.path.join(self.mods, "Mod")), self.main_checkout)
        self.assertEqual(os.readlink(os.path.join(self.mods, "TestMod")), self.other_branch,
                         "the link belonged to another branch before us; it goes back to them")
        self.assertEqual(read(os.path.join(self.assemblies, "Mod.dll")), "MAIN DLL")
        self.assertEqual(read(os.path.join(self.assemblies, "Mod.pdb")), "MAIN PDB")

    def test_recovery_leaves_a_file_someone_curated_since(self):
        """Partial recovery is a reported outcome, not an all-or-nothing abort."""
        self.simulate_run_then_crash()
        write(self.config, "<curated>a list someone rebuilt by hand</curated>")

        results = asset_claims.restore(self.ledger, guard=True)
        by_path = {r["path"]: r for r in results}
        self.assertEqual(by_path[self.config]["action"], "skipped")
        self.assertEqual(read(self.config), "<curated>a list someone rebuilt by hand</curated>")
        # Everything else still came back.
        self.assertEqual(read(os.path.join(self.assemblies, "Mod.dll")), "MAIN DLL")
        self.assertEqual(os.readlink(os.path.join(self.mods, "TestMod")), self.other_branch)


class LedgerFormat(ClaimTestCase):
    def test_unknown_version_is_refused_rather_than_half_applied(self):
        with open(self.ledger, encoding="utf-8") as handle:
            ledger = json.load(handle)
        ledger["version"] = asset_claims.LEDGER_VERSION + 1
        with open(self.ledger, "w", encoding="utf-8") as handle:
            json.dump(ledger, handle)

        with self.assertRaises(asset_claims.ClaimError):
            asset_claims.restore(self.ledger, guard=True)

    def test_restore_cli_deletes_a_fully_rolled_back_ledger(self):
        dest = write(self.path("f"), "ORIGINAL")
        asset_claims.claim_path(self.ledger, dest=dest, src=write(self.path("s"), "NEW"))

        rc = asset_claims.main(["--ledger", self.ledger, "restore"])
        self.assertEqual(rc, 0)
        self.assertFalse(os.path.exists(self.ledger),
                         "a closed ledger must not look like an abandoned one to the next run")

    def test_restore_cli_keeps_a_ledger_it_could_not_finish(self):
        dest = write(self.path("f"), "ORIGINAL")
        asset_claims.claim_path(self.ledger, dest=dest, src=write(self.path("s"), "NEW"))
        write(dest, "EDITED SINCE")

        rc = asset_claims.main(["--ledger", self.ledger, "restore", "--guard"])
        self.assertEqual(rc, 1)
        self.assertTrue(os.path.exists(self.ledger))


if __name__ == "__main__":
    unittest.main()
