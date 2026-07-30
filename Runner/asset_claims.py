#!/usr/bin/env python3
"""Runner/asset_claims.py — the ledger that makes run_test.sh's global mutations undoable.

WHY THIS EXISTS
---------------
`run_test.sh` already takes an exclusive `flock` so two runs cannot overlap. That lock protects
the *game*, and it protects the files the script itself swaps (ModsConfig.xml, Prefs.xml,
Saves/autostart.rws). It historically did NOT protect the one thing agents actually collide over:
the **built assembly** of the mod under test.

`RimWorld/Mods/<Mod>` is a permanent symlink to a mod's main checkout, and `setup_symlink`
deliberately never repoints a link it did not create — so pointing the runner at a git worktree does
not change which DLL the game loads. Every agent therefore hand-copied its worktree build over the
main checkout's `1.6/Assemblies/` before invoking the runner, and restored it afterwards. Both of
those steps happened *outside* the lock, which produced two failures we actually hit:

  1. Agent A installs its DLL, then blocks on the lock held by B. B's game boots against A's
     assembly. B measures A's build, and reports the frames as proof of B's fix.
  2. A run dies at mod load. Its teardown never completes, so the branch DLL — and a `Mods/TestMod`
     symlink pointing at a since-merged worktree — survive into the next run, which silently loads
     them. The tell is maddeningly indirect: only probes *newer* than the stale branch go missing,
     which reads like a registration bug in the mod's own code.

The fix is not more care from callers. It is to move the install inside the lock and make every
global mutation a **claim**: a recorded, reversible, hash-identified swap. This module is that
record. `run_test.sh` claims a path before writing it and restores every claim on teardown; a
ledger left behind by a crashed run is found by the *next* run (which, holding the lock, knows the
ledger cannot belong to anyone live) and rolled back before it does anything else.

THE HASH GUARD
--------------
Recovering another run's ledger is restoring a backup that may be hours old, so it must not
clobber a file someone has legitimately changed since. Every claim records the SHA-256 of what the
run *installed*. On recovery a claim is rolled back only if the file still hashes to that value —
i.e. nobody has touched it since the crash. Anything else is left exactly as-is and reported with
its backup path, because a wrong automatic restore is worse than a loud manual one.

This distinction is why `restore` takes `--guard` rather than always guarding: at that run's own
teardown it owns everything it claimed and restores unconditionally (RimWorld rewrites the whole of
Prefs.xml on exit, so a guarded teardown would refuse to undo the `<devMode>` seed every time).

Kept as a standalone script rather than inlined into run_test.sh because this is the part with real
branching to get wrong, and it is worth having offline tests over — see
Tests/runner/test_asset_claims.py, which exercises it with no game, no lock and no filesystem
outside a tmpdir. Standard library only, python3 being a hard dependency of the runner already.
"""

import argparse
import hashlib
import json
import os
import shutil
import sys

# Ledger format version. Bumped if the claim shape changes incompatibly; a run that finds a ledger
# it cannot read must say so rather than half-restore it from a guessed schema.
LEDGER_VERSION = 1

# Claim kinds. A "path" claim covers a single file the run overwrites (or creates); a "symlink"
# claim covers a Mods/ entry the run points at a folder of its choosing. Directories are not a kind
# of their own — a directory overlay expands to one path claim per file at claim time, so restore
# never has to reason about a tree, only about files it can name and hash.
KIND_PATH = "path"
KIND_SYMLINK = "symlink"

# Prior-state markers for a claimed path. Distinguishing "there was nothing here" from "there was
# something here" is the whole reason teardown can be exact: restoring the first case means deleting
# what we put there, and the original code's lack of that distinction is what once made a missing
# backup mean "delete the user's real save".
PRIOR_ABSENT = "absent"
PRIOR_FILE = "file"
PRIOR_SYMLINK = "symlink"
PRIOR_DIR = "dir"


class ClaimError(Exception):
    """A claim could not be taken. Always fatal to the run: an unrecorded mutation is one teardown
    cannot undo, which is precisely the state this module exists to make impossible."""


# ---------------------------------------------------------------------------
# Small filesystem helpers
# ---------------------------------------------------------------------------
def sha256_of(path):
    """Hash a regular file; None for anything that isn't one (absent, symlink, directory).

    Returning None rather than raising keeps the guard logic honest at the call site: "no hash"
    flows through as "cannot prove this is untouched", which the guard then refuses to act on.
    """
    if not os.path.isfile(path) or os.path.islink(path):
        return None
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def prior_kind_of(path):
    """Classify what currently occupies a path, before we take it over.

    islink is checked first and separately from isfile/isdir: a symlink to a directory answers True
    to both, and a run that recorded such a link as a directory would try to restore it by copying a
    tree back over the user's real mod folder.
    """
    if os.path.islink(path):
        return PRIOR_SYMLINK
    if not os.path.exists(path):
        return PRIOR_ABSENT
    if os.path.isdir(path):
        return PRIOR_DIR
    return PRIOR_FILE


def files_under(root):
    """Every regular file under `root`, as paths relative to it, sorted for determinism.

    Sorted because the ledger is a run artifact people read and diff; an overlay that recorded its
    files in filesystem order would produce a different ledger for the same install.
    """
    found = []
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in sorted(filenames):
            absolute = os.path.join(dirpath, name)
            if os.path.isfile(absolute) and not os.path.islink(absolute):
                found.append(os.path.relpath(absolute, root))
    return sorted(found)


# ---------------------------------------------------------------------------
# Ledger I/O
# ---------------------------------------------------------------------------
def load_ledger(path):
    with open(path, encoding="utf-8") as handle:
        ledger = json.load(handle)
    version = ledger.get("version")
    if version != LEDGER_VERSION:
        raise ClaimError(
            f"ledger {path} is version {version}, this script speaks version {LEDGER_VERSION} — "
            "restore it by hand rather than letting a mismatched schema half-undo it"
        )
    return ledger


def save_ledger(path, ledger):
    """Write the ledger atomically.

    The rename matters more than it looks: the ledger is read by a *later* run to decide what to
    roll back, so a torn write — a crash mid-`json.dump` — would hand that run a truncated file it
    cannot parse, at exactly the moment it is trying to clean up after a crash. Same-directory
    temp file so the rename stays on one filesystem and is therefore atomic.
    """
    tmp = f"{path}.tmp.{os.getpid()}"
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump(ledger, handle, indent=2, sort_keys=True)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(tmp, path)


def append_claim(ledger_path, claim):
    ledger = load_ledger(ledger_path)
    ledger["claims"].append(claim)
    save_ledger(ledger_path, ledger)


# ---------------------------------------------------------------------------
# Taking claims
# ---------------------------------------------------------------------------
def backup_slot(ledger, name_hint):
    """Reserve a unique filename inside the run's backup dir.

    Uniquified by claim index rather than by the source path's basename because two claims routinely
    share one (`.../1.6/Assemblies/X.dll` from a worktree and from the main checkout), and a
    collision here would silently make one claim restore the other's content.
    """
    backup_dir = ledger["backup_dir"]
    os.makedirs(backup_dir, mode=0o700, exist_ok=True)
    safe = "".join(c if c.isalnum() or c in "._-" else "_" for c in os.path.basename(name_hint))
    return os.path.join(backup_dir, f"{len(ledger['claims']):03d}-{safe or 'claim'}")


def claim_path(ledger_path, dest, src=None):
    """Record `dest`'s current state, then (optionally) install `src` over it.

    `src=None` is the "I am about to rewrite this file myself" case — ModsConfig.xml, which the
    runner generates, and Prefs.xml, which it edits in place. Those callers claim first, write, then
    `seal` to record the hash of what they ended up with. Splitting it that way is what lets a single
    ledger cover both the files we copy in and the files we compose.
    """
    ledger = load_ledger(ledger_path)
    dest = os.path.abspath(dest)

    prior = prior_kind_of(dest)
    if prior == PRIOR_DIR:
        raise ClaimError(f"refusing to claim {dest}: it is a directory, not a file")

    claim = {
        "kind": KIND_PATH,
        "path": dest,
        "prior": prior,
        "backup": None,
        "prior_target": None,
        "installed_sha256": None,
    }

    if prior == PRIOR_SYMLINK:
        # A claimed path that is a symlink is preserved as a link, not as a copy of its target:
        # restoring it by writing a regular file would silently break whatever the link pointed at.
        claim["prior_target"] = os.readlink(dest)
    elif prior == PRIOR_FILE:
        backup = backup_slot(ledger, dest)
        shutil.copy2(dest, backup)
        claim["backup"] = backup

    if src is not None:
        src = os.path.abspath(src)
        if not os.path.isfile(src):
            raise ClaimError(f"install source {src} is not a regular file")
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        # The link, if any, is removed rather than followed: copying onto a symlink writes through
        # it and would modify the link's target — a file this run never claimed and cannot restore.
        if os.path.islink(dest):
            os.unlink(dest)
        shutil.copy2(src, dest)
        claim["installed_sha256"] = sha256_of(dest)

    ledger["claims"].append(claim)
    save_ledger(ledger_path, ledger)
    return claim


def claim_overlay(ledger_path, src_dir, dest_dir):
    """Overlay every file in `src_dir` onto `dest_dir`, one claim per file.

    This is the branch-build install: `<worktree>/1.6/Assemblies` over
    `<main checkout>/1.6/Assemblies`. Directory-granular by design — copying the whole build output
    rather than a named DLL is what makes the stale-`.pdb` crash structurally impossible. (Mono loads
    the symbol file during assembly load and faults on a mismatch: RimWorld dies with
    `Caught fatal signal - signo:11` right after `RWTH: harness loaded`, with the only clue a single
    `Symbol file ... doesn't match image ...` line in Player.log. Copying the directory means the
    pair can never be mismatched.)

    An overlay, not a replacement: files present in dest but absent from src are left alone. The
    caller is installing a build, not asserting the destination should contain nothing else.
    """
    src_dir = os.path.abspath(src_dir)
    dest_dir = os.path.abspath(dest_dir)
    if not os.path.isdir(src_dir):
        raise ClaimError(f"overlay source {src_dir} is not a directory")
    if not os.path.isdir(dest_dir):
        raise ClaimError(f"overlay destination {dest_dir} is not a directory")

    members = files_under(src_dir)
    if not members:
        raise ClaimError(
            f"overlay source {src_dir} contains no files — nothing to install. "
            "If this is a worktree, has its build.sh been run?"
        )

    claimed = []
    for relative in members:
        claimed.append(
            claim_path(
                ledger_path,
                dest=os.path.join(dest_dir, relative),
                src=os.path.join(src_dir, relative),
            )
        )
    return claimed


def claim_symlink(ledger_path, link_path, target):
    """Point `link_path` at `target` for the duration of the run, recording whatever was there.

    Unlike the old `setup_symlink`, an existing link aimed somewhere *else* is repointed rather than
    left alone. Leaving it was the cautious-looking choice and the wrong one: every worktree's probe
    mod has the same basename and packageId (`TestMod`), so a link left over from a crashed run on
    another branch wins over the `--mod` this run was given, and the run silently exercises a
    different branch's probes. Repointing is safe precisely because the caller holds the run lock —
    no other run exists to be disturbed — and the prior target is recorded, so teardown puts it back.

    A real directory is refused rather than moved aside. That is a genuinely installed mod, it may be
    large, and silently testing against a copy of the mod other than the one named on the command
    line is the exact class of bug this module exists to end.
    """
    ledger = load_ledger(ledger_path)
    link_path = os.path.abspath(link_path)
    target = os.path.abspath(target)

    prior = prior_kind_of(link_path)
    if prior == PRIOR_DIR:
        raise ClaimError(
            f"refusing to claim {link_path}: a real directory is installed there. "
            f"The run would load it instead of {target}. Move or remove it first."
        )
    if prior == PRIOR_FILE:
        raise ClaimError(f"refusing to claim {link_path}: a regular file is in the way")

    claim = {
        "kind": KIND_SYMLINK,
        "path": link_path,
        "prior": prior,
        "backup": None,
        "prior_target": os.readlink(link_path) if prior == PRIOR_SYMLINK else None,
        "installed_target": target,
    }

    # An already-correct link is still recorded, with prior_target == installed_target, so restore
    # is a no-op for it. Recording it costs nothing and makes the ledger a complete statement of what
    # the run depends on, which is what makes it readable as evidence after a confusing result.
    if prior == PRIOR_SYMLINK:
        os.unlink(link_path)
    os.symlink(target, link_path)

    ledger["claims"].append(claim)
    save_ledger(ledger_path, ledger)
    return claim


def seal(ledger_path, dest):
    """Record the hash of a claimed path *after* the caller has written it.

    The other half of the `claim_path(src=None)` split. Without this a self-composed file (the
    generated ModsConfig.xml) would carry no installed hash, and the recovery guard would have
    nothing to compare against — so it would refuse to restore the one file whose silent persistence
    caused the worst outage we have had (an 828-mod list replaced by a 14-mod test list for hours,
    with every later run dutifully backing up the test list).
    """
    ledger = load_ledger(ledger_path)
    dest = os.path.abspath(dest)
    for claim in reversed(ledger["claims"]):
        if claim["kind"] == KIND_PATH and claim["path"] == dest:
            claim["installed_sha256"] = sha256_of(dest)
            save_ledger(ledger_path, ledger)
            return claim
    raise ClaimError(f"cannot seal {dest}: it was never claimed")


# ---------------------------------------------------------------------------
# Restoring
# ---------------------------------------------------------------------------
def _restore_path_claim(claim, guard):
    """Undo one path claim. Returns (action, detail) for reporting."""
    path = claim["path"]
    installed = claim.get("installed_sha256")

    if guard:
        # "Untouched since we installed it" is the only condition under which rolling back another
        # run's ledger is safe. No recorded hash means we cannot establish it — treat that as
        # touched, not as permission.
        current = sha256_of(path)
        if installed is None:
            return ("skipped", "no installed hash recorded — cannot prove it is untouched")
        if current != installed:
            return ("skipped", "changed since it was installed — left alone")

    prior = claim["prior"]
    if prior == PRIOR_ABSENT:
        if os.path.lexists(path):
            os.unlink(path)
        return ("removed", "nothing was there before")
    if prior == PRIOR_SYMLINK:
        if os.path.lexists(path):
            os.unlink(path)
        os.symlink(claim["prior_target"], path)
        return ("restored", f"symlink -> {claim['prior_target']}")

    backup = claim.get("backup")
    if not backup or not os.path.isfile(backup):
        return ("failed", f"backup missing at {backup!r} — original content is lost")
    if os.path.islink(path):
        os.unlink(path)
    shutil.copy2(backup, path)
    return ("restored", f"from {backup}")


def _restore_symlink_claim(claim, guard):
    path = claim["path"]

    if guard:
        # The symlink equivalent of the hash guard: if it no longer points where we left it,
        # something else has claimed it and we are not the ones to decide.
        current = os.readlink(path) if os.path.islink(path) else None
        if current != claim.get("installed_target"):
            return ("skipped", f"now points at {current!r} — left alone")

    prior = claim["prior"]
    if prior == PRIOR_ABSENT:
        if os.path.lexists(path):
            os.unlink(path)
        return ("removed", "nothing was there before")

    if os.path.lexists(path):
        os.unlink(path)
    os.symlink(claim["prior_target"], path)
    return ("restored", f"-> {claim['prior_target']}")


def restore(ledger_path, guard):
    """Roll back every claim, newest first, and report what happened to each.

    Reverse order because claims nest in practice: an overlay's files are claimed after the
    directory they land in has been established, and undoing in reverse keeps each rollback looking
    at the state its own claim was taken against.

    Never raises on a single claim's failure. A teardown that aborted halfway through would leave
    *more* global state swapped than one that pushed on and reported, and the report is the thing a
    reader needs in order to finish by hand.
    """
    ledger = load_ledger(ledger_path)
    results = []
    for claim in reversed(ledger["claims"]):
        try:
            if claim["kind"] == KIND_SYMLINK:
                action, detail = _restore_symlink_claim(claim, guard)
            else:
                action, detail = _restore_path_claim(claim, guard)
        except OSError as exc:
            action, detail = ("failed", str(exc))
        results.append({"path": claim["path"], "kind": claim["kind"], "action": action, "detail": detail})
    return results


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def cmd_begin(args):
    ledger = {
        "version": LEDGER_VERSION,
        "run_id": args.run_id,
        "pid": os.getpid(),
        "backup_dir": os.path.abspath(args.backup_dir),
        "claims": [],
    }
    os.makedirs(os.path.dirname(os.path.abspath(args.ledger)) or ".", exist_ok=True)
    os.makedirs(ledger["backup_dir"], mode=0o700, exist_ok=True)
    save_ledger(args.ledger, ledger)
    return 0


def cmd_claim(args):
    claim_path(args.ledger, dest=args.dest, src=args.src)
    return 0


def cmd_overlay(args):
    claimed = claim_overlay(args.ledger, src_dir=args.src, dest_dir=args.dest)
    for claim in claimed:
        print(f"  installed {os.path.basename(claim['path'])} -> {os.path.dirname(claim['path'])}")
    return 0


def cmd_symlink(args):
    claim = claim_symlink(args.ledger, link_path=args.path, target=args.target)
    if claim["prior"] == PRIOR_SYMLINK and claim["prior_target"] != claim["installed_target"]:
        # Loud on purpose. A repointed link means a previous run's leftovers were about to be loaded
        # instead of what this run was given — worth a line in the log even though we fixed it.
        print(f"  repointed {claim['path']}: was -> {claim['prior_target']}")
    return 0


def cmd_seal(args):
    seal(args.ledger, args.dest)
    return 0


def cmd_restore(args):
    results = restore(args.ledger, guard=args.guard)
    failures = 0
    for result in results:
        if result["action"] in ("skipped", "failed"):
            failures += 1
            print(f"  {result['action'].upper()}: {result['path']} — {result['detail']}")
        elif args.verbose:
            print(f"  {result['action']}: {result['path']} — {result['detail']}")
    print(f"  {len(results)} claim(s) processed, {failures} not rolled back")
    if not failures and not args.keep:
        os.unlink(args.ledger)
    return 1 if failures else 0


def cmd_status(args):
    if not os.path.exists(args.ledger):
        print("no ledger — no run currently holds any global asset")
        return 0
    ledger = load_ledger(args.ledger)
    print(f"ledger {args.ledger}")
    print(f"  run_id     {ledger['run_id']}")
    print(f"  pid        {ledger['pid']}")
    print(f"  backups    {ledger['backup_dir']}")
    for claim in ledger["claims"]:
        if claim["kind"] == KIND_SYMLINK:
            print(f"  symlink    {claim['path']} -> {claim['installed_target']} (was: "
                  f"{claim['prior_target'] or claim['prior']})")
        else:
            print(f"  path       {claim['path']} (was: {claim['backup'] or claim['prior']})")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--ledger", required=True, help="path to the claim ledger JSON")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("begin", help="start a fresh ledger for this run")
    p.add_argument("--run-id", required=True)
    p.add_argument("--backup-dir", required=True)
    p.set_defaults(func=cmd_begin)

    p = sub.add_parser("claim", help="claim one path, optionally installing a file over it")
    p.add_argument("--dest", required=True)
    p.add_argument("--src", default=None, help="omit if the caller writes DEST itself, then 'seal'")
    p.set_defaults(func=cmd_claim)

    p = sub.add_parser("overlay", help="claim+install every file of SRC dir onto DEST dir")
    p.add_argument("--src", required=True)
    p.add_argument("--dest", required=True)
    p.set_defaults(func=cmd_overlay)

    p = sub.add_parser("symlink", help="claim a symlink path and point it at TARGET")
    p.add_argument("--path", required=True)
    p.add_argument("--target", required=True)
    p.set_defaults(func=cmd_symlink)

    p = sub.add_parser("seal", help="record the hash of a claimed path the caller has now written")
    p.add_argument("--dest", required=True)
    p.set_defaults(func=cmd_seal)

    p = sub.add_parser("restore", help="roll every claim back")
    p.add_argument("--guard", action="store_true",
                   help="only roll back items still identical to what was installed (use when "
                        "recovering ANOTHER run's abandoned ledger)")
    p.add_argument("--keep", action="store_true", help="do not delete the ledger afterwards")
    p.add_argument("--verbose", action="store_true")
    p.set_defaults(func=cmd_restore)

    p = sub.add_parser("status", help="print what a ledger currently holds")
    p.set_defaults(func=cmd_status)

    args = parser.parse_args(argv)
    try:
        return args.func(args)
    except ClaimError as exc:
        print(f"asset_claims: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
