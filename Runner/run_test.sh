#!/usr/bin/env bash
# Runner/run_test.sh — Live RimWorld scenario runner.
#
# Runs ONE scenario or a SUITE of them. A RimWorld boot costs minutes and a step costs
# milliseconds, so a suite runs every scenario inside a single game load, reloading the fixture
# mid-session between the ones that mutated the map (see DESIGN.md, "Batching scenarios into one
# load"). One scenario on the command line behaves exactly as it always has, report shape included.
#
# What this does:
#   0. Rolls back any claim ledger a previous run abandoned (see "Asset claims" below), then opens
#      one of its own. Every global mutation from here on is recorded in it before it happens.
#   1. Claims a symlink under RimWorld's Mods folder for RimWorldTestHarness and every --mod folder:
#      records whatever was there, points it at the folder this run was given, and puts the previous
#      state back on teardown. A link left behind by another branch's crashed run is REPOINTED, not
#      obeyed — every worktree's probe mod shares one basename, so obeying it silently ran the wrong
#      branch's probes.
#   2. Backs up the real ModsConfig.xml, Prefs.xml, and any existing Saves/autostart.rws, then seeds
#      <devMode>True</devMode> into Prefs.xml — vanilla's autostart check is gated on it and runs
#      before any mod assembly loads, so it cannot be forced from inside the game (see seed_devmode).
#   3. Writes a minimal ModsConfig.xml: Core + installed DLCs + brrainz.harmony + the union of
#      every selected scenario's requiredMods + each --mod's packageId (read from its own
#      About/About.xml) + joof.rimworldtestharness, which goes after them so its patches wrap
#      theirs — except any --mod that itself depends on the harness (a probe bridge), which must
#      load after it or its assembly cannot resolve IProbe.
#   4. Copies the scenarios' Fixtures/<saveFile> to Saves/autostart.rws — RimWorld's own vanilla
#      autostart mechanism (Root_Entry.Start -> SaveGameFilesUtility.GetAutostartSaveFile, gated
#      on Prefs.DevMode which Patch_ForceDevMode forces true while a scenario is active) loads it
#      with no custom load-driving code needed. See DESIGN.md.
#   4b. Installs any --install / --mod-overlay build output over its destination, under the lock,
#      recording each file so teardown puts the previous build back byte-for-byte.
#   5. Launches RimWorldLinux with RWTH_SCENARIO (single) or RWTH_SUITE (suite) plus RWTH_REPORT,
#      GPU-rendering (no -batchmode/-nographics — Screenshot steps need a real rendered frame),
#      reusing MissileGirl/TestMods/run_test.sh's --no-sandbox + retry-on-early-crash shape.
#   6. Waits for the report file to appear, gated on RimWorld staying alive and a timeout.
#   7. Parses the JSON report, prints each probe's pass/fail and any screenshot paths, exits 0
#      only if Pass == true (for a suite: only if every scenario passed and there were no
#      suite-level errors).
#   8. Stitches any Timelapse frame sequences into videos.
#   9. Rolls the whole ledger back — config, save, symlinks, installed assemblies — and deletes it
#      (unless --no-teardown).
#
# Usage:
#   ./run_test.sh <scenario.json> [flags]                  # single scenario (unchanged behaviour)
#   ./run_test.sh <a.json> <b.json> [<c.json> ...] [flags] # suite, one game load
#   ./run_test.sh --suite <suite.txt> [flags]              # suite from a list file
#   ./run_test.sh ../SomeMod/Tests/Scenarios/*.json        # the shell does the globbing
#   ./run_test.sh --mod ../SomeMod --mod ../SomeMod/TestMod ../SomeMod/Tests/Scenarios/x.json
#
# Flags:
#   --mod <folder>         a mod to activate alongside the harness — the mod whose probes/features
#                          the scenarios exercise. Repeatable, and optional: this repo's own
#                          Scenarios/ use only vanilla defs and need no --mod at all. Pass a mod and
#                          its probe-bridge folder in the order you want them loaded.
#   --mod-overlay <folder> install <folder>/1.6/Assemblies over the assemblies of the ALREADY
#                          INSTALLED mod with the same packageId, for the duration of the run. This
#                          is how you live-test a git worktree: Mods/<Mod> is a permanent symlink to
#                          the main checkout, so naming a worktree with --mod does not change which
#                          DLL the game loads. Repeatable. Do NOT also pass the worktree as --mod —
#                          that gives two mod folders sharing one packageId.
#   --install <src>:<dst>  lower-level form of the same thing: overlay directory <src> onto directory
#                          <dst> for the run, then restore <dst> exactly. Repeatable. Neither path
#                          may contain ':'.
#   --recover-only         roll back an abandoned ledger, report, and exit without running anything.
#   --no-teardown          leave symlinks/ModsConfig/autostart.rws in place afterwards
#   --delete-frames        delete timelapse PNGs once stitched
#   --isolation=POLICY     auto (default) | always | never — how hard a suite works to isolate one
#                          scenario from the next. See Shared/SuitePlan.cs.
#   --without-dlc <id>     leave an installed DLC out of the run's ModsConfig (e.g.
#                          ludeon.rimworld.odyssey). Repeatable. For exercising a scenario's
#                          skip-without-the-DLC path on a machine that owns the DLC.
#   --profiler             activate Dubs Performance Analyzer (Workshop 2038874626) for this run, so
#                          Profile steps have something to measure with. OFF BY DEFAULT AND MEANT TO
#                          STAY THAT WAY: the analyzer rewrites the body of every Harmony-patched
#                          method in the load, so every timing number in a run that has it loaded —
#                          including ordinary probes that have nothing to do with profiling — is a
#                          number measured through an instrumented build. Use it for the run that
#                          answers a performance question, never for the pinned runs you compare
#                          against. Fails if the analyzer is not installed.
#   --print-config         print the resolved paths for this run and exit without touching anything
#
# A suite list file is one scenario path per line; '#' starts a whole-line comment, and relative
# paths resolve against the list file's own directory (Shared/SuiteList.cs parses it).
#
# All scenarios in a suite must declare the same saveFile, because the save is chosen once at boot
# and reloaded from mid-run — mixed fixtures are rejected rather than silently run against whichever
# one came first.
#
# Notes:
#   - This run only ever signals the RimWorldLinux process IT started, by PID (SIGTERM, escalating to
#     SIGKILL). It deliberately does NOT pkill by name: a second instance — another agent's run, or
#     the user just playing — must survive us. (If a name-based kill is ever reintroduced, note that
#     -x is required and -f is wrong: -f matches this script's own command line.)
#   - Instead of killing strays, the run refuses to start while any RimWorldLinux is alive, and takes
#     an exclusive flock so two runs cannot overlap. See "Run guard" below.
#   - Everything mutable this run owns (backups, Player.log, stderr) lives under one per-run scratch
#     directory, so overlapping runs cannot restore each other's backup over the real config.
#   - Asset claims: the lock stops two runs overlapping, but it never covered the assets a caller
#     swapped in *around* the run — chiefly the branch build copied over the main checkout's
#     1.6/Assemblies so the Mods/ symlink would resolve to it. That copy sat outside the lock, so a
#     run could boot against another agent's assembly and report the resulting frames as its own; and
#     a run that died left the branch DLL and a stale Mods/TestMod behind for the next one to load
#     silently. --install/--mod-overlay move the copy inside the lock, and Runner/asset_claims.py
#     records every swap (config, save, symlinks, assemblies) in a ledger at $RWTH_LEDGER_FILE so
#     teardown is exact and a crashed run's leftovers are rolled back by the next run rather than
#     inherited. Recovery is hash-guarded: anything edited since it was installed is reported, not
#     overwritten. `asset_claims.py --ledger <path> status` says what is currently claimed.
#   - This still relaunches RimWorldLinux and temporarily swaps the real
#     ModsConfig.xml/Saves/autostart.rws (both backed up and restored) unless RWTH_ISOLATE_SAVEDATA=1
#     — same blast radius as MissileGirl/TestMods/run_test.sh.

set -euo pipefail

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
RIMWORLD="/home/deck/.local/share/Steam/steamapps/common/RimWorld"
MODS_DIR="$RIMWORLD/Mods"

# Where Steam puts subscribed RimWorld mods. Only consulted by --profiler, which is the one flag that
# names a specific Workshop mod rather than taking a folder from the caller.
WORKSHOP_DIR="${RWTH_WORKSHOP_DIR:-$RIMWORLD/../../workshop/content/294100}"

# Per-run scratch. Every mutable file this run owns hangs off here. The old fixed /tmp names
# (/tmp/ModsConfig_rwth.bak.xml etc.) were a correctness bug even without deliberate parallelism: two
# overlapping runs shared one backup, so whichever finished last restored the other's snapshot over
# the user's real ModsConfig.xml. $$ makes this unique per invocation even within the same second.
RUN_ID="$(date +%Y%m%d-%H%M%S)-$$"
RUN_TMP_DIR="${RWTH_RUN_TMP_DIR:-${TMPDIR:-/tmp}/rwth-run-$RUN_ID}"

# The game's save-data root (holds Config/ModsConfig.xml, Saves/, Screenshots/). Single overridable
# variable; the default is exactly what this script has always used, so behaviour is unchanged.
REAL_CONFIG_DIR="${RWTH_CONFIG_DIR:-/home/deck/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios}"

# OPT-IN, OFF BY DEFAULT: RWTH_ISOLATE_SAVEDATA=1 gives the run its own save-data root instead of
# mutating the user's. This uses RimWorld's OWN -savedatafolder= command-line arg
# (Verse.GenFilePaths.SaveDataFolderPath checks GenCommandLine.TryGetCommandLineArg("savedatafolder")
# and, when set, uses it verbatim in place of Application.persistentDataPath) — not $XDG_CONFIG_HOME,
# which we could not prove this Unity build honours. Because the arg is first-party and logs
# "Save data folder overridden to <path>", the run can and does assert afterwards that the game
# actually used our directory (assert_savedata_folder below) rather than silently hoping.
# Still opt-in because a fresh root is a behaviour change a live run has to validate: RimWorld sees
# no Screenshots/, an unfamiliar Saves/, and only the Config/ we seed in.
ISOLATE_SAVEDATA="${RWTH_ISOLATE_SAVEDATA:-0}"
if [[ "$ISOLATE_SAVEDATA" == "1" ]]; then
    CONFIG_DIR="$RUN_TMP_DIR/savedata"
else
    CONFIG_DIR="$REAL_CONFIG_DIR"
fi

# Player.log moves into the run's scratch dir too. It has to: the script greps it for "RWTH: harness
# loaded" / "RWTH: scenario complete", and a shared log means one run reads another's markers. Unity's
# -logfile (already passed at launch) is what makes this a one-line change.
PLAYER_LOG="$RUN_TMP_DIR/Player.log"
MODSCONFIG="$CONFIG_DIR/Config/ModsConfig.xml"

# Prefs.xml is swapped for exactly one reason: <devMode>. Vanilla's autostart-save mechanism is gated
# on Prefs.DevMode (Verse.SaveGameFilesUtility.GetAutostartSaveFile returns null when it's false), so
# with dev mode off the game boots to the main menu and the fixture never loads — every scenario then
# either times out or silently measures whatever map a human loaded by hand.
#
# This USED to be Patch_ForceDevMode's job, and that patch cannot do it. Verse.Root.Start() only
# QUEUES PlayDataLoader.LoadAllPlayData() as an async long event; LoadedModManager.LoadAllActiveMods()
# and StaticConstructorOnStartupUtility.CallAll() both run inside it. Root_Entry.Start() checks for the
# autostart save synchronously on the line after base.Start() returns — before any mod assembly is
# loaded, before Harmony has patched anything, and so before HarnessRuntime.ForceDevMode can be set.
# Runs only ever worked because the user's ambient devMode happened to be true. Seeding it here is what
# makes the mechanism actually hold; the patch stays for DevMode reads later in the boot.
PREFS="$CONFIG_DIR/Config/Prefs.xml"

SAVES_DIR="$CONFIG_DIR/Saves"
AUTOSTART_SAVE="$SAVES_DIR/autostart.rws"
RIMWORLD_STDERR="$RUN_TMP_DIR/rimworld_stderr.log"

# Where the claim ledger's backups live. Per-run like everything else mutable, so a ledger abandoned
# by a dead run still points at its own copies and a later run's claims cannot overwrite them.
CLAIM_BACKUP_DIR="$RUN_TMP_DIR/claims"

# Deliberately NOT per-run: the point of the lock is that every run contends for the same file.
LOCK_FILE="${RWTH_LOCK_FILE:-${TMPDIR:-/tmp}/rwth-run-$(id -u).lock}"

# Also deliberately NOT per-run, and for the same reason: the ledger records what is currently
# swapped in on this machine, so the next run has to find it at a path it can predict without knowing
# who wrote it. It exists only while a run holds something — created after the lock is taken, deleted
# once teardown has rolled everything back. A ledger present while we hold the lock therefore means
# exactly one thing: a previous run died without cleaning up. (Only "exactly one thing" because we
# read it under the lock. A run started with a different RWTH_LOCK_FILE but the same ledger path
# breaks that inference — don't override one without the other.)
LEDGER_FILE="${RWTH_LEDGER_FILE:-${TMPDIR:-/tmp}/rwth-claims-$(id -u).json}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"                       # RimWorldTestHarness/
REPORTS_DIR="$SCRIPT_DIR/reports"

log()  { echo "[run_test] $*"; }
fail() {
    echo "[run_test] FAIL: $*" >&2
    sleep 5
    exit 1
}

# ---------------------------------------------------------------------------
# Args
# ---------------------------------------------------------------------------
SCENARIOS=()
SUITE_LIST_IN=""
NO_TEARDOWN=0
DELETE_FRAMES=0
ISOLATION="auto"
PRINT_CONFIG=0
RECOVER_ONLY=0

# --profiler: put Dubs Performance Analyzer in this run's ModsConfig. Deliberately a flag of its own
# rather than "just pass it as --mod", for two reasons. It is a Workshop mod with no folder the caller
# would know the path of, and — the important one — it needs to load EARLY: it patches the Harmony
# constructor to record which mod owns which patch, so any mod that builds its Harmony instance before
# the analyzer loads ends up with its patches attributed to nobody. A --mod would land after the
# required mods, which is too late.
PROFILER=0

# Build outputs to install over an existing installation for the life of the run, as "src:dest"
# directory pairs. --mod-overlay resolves to entries here once packageIds are known (see
# resolve_mod_overlays); --install is the same thing with both paths spelled out.
#
# Directory pairs rather than file pairs on purpose. The concrete case is a mod's 1.6/Assemblies, and
# copying only the .dll out of one leaves a mismatched .pdb behind — Mono loads the symbol file during
# assembly load and faults, so RimWorld dies with signo:11 straight after "RWTH: harness loaded" and
# it reads as a crash in the mod's own patch code. A whole-directory overlay cannot produce that pair.
INSTALL_PAIRS=()
MOD_OVERLAY_DIRS=()

# Mod folders to activate alongside the harness — the mods whose probes/features the scenarios
# exercise. Repeatable, and legitimately empty: the harness's own scenarios under Scenarios/ use
# only vanilla defs, so a bare run needs no mod under test at all. Passed as paths rather than
# packageIds because the run has to symlink the folder into Mods/ as well as name it in ModsConfig,
# and only the folder knows both.
MODS_UNDER_TEST=()

# DLC packageIds to leave OUT of the run's ModsConfig even though they are installed. Exists so a
# scenario's degrade-without-the-DLC path can actually be exercised on a machine that owns the DLC —
# which is the only machine anyone develops such a scenario on. Without it, a step's "skip when the
# DLC is absent" branch is code nobody can run, and an untested skip is the same green-means-less
# failure as an untested assert. Deactivating rather than uninstalling is exactly right: ModsConfig
# is what ModsConfig.OdysseyActive and friends read, not what is on disk.
EXCLUDED_DLC=()

abspath() { echo "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"; }

usage() {
    echo "[run_test] usage: run_test.sh <scenario.json> [more.json ...] [--suite <list.txt>]" >&2
    echo "[run_test]        [--mod <mod-folder>]... [--no-teardown] [--delete-frames]" >&2
    echo "[run_test]        [--isolation=auto|always|never] [--without-dlc <packageId>]..." >&2
    echo "[run_test]        [--mod-overlay <worktree>]... [--install <src-dir>:<dest-dir>]..." >&2
    echo "[run_test]        [--profiler] [--print-config] [--recover-only]" >&2
    exit 2
}

while (( $# )); do
    case "$1" in
        --no-teardown) NO_TEARDOWN=1 ;;
        --without-dlc) shift; EXCLUDED_DLC+=("$(echo "${1:-}" | tr '[:upper:]' '[:lower:]')") ;;
        --without-dlc=*) EXCLUDED_DLC+=("$(echo "${1#--without-dlc=}" | tr '[:upper:]' '[:lower:]')") ;;
        --delete-frames) DELETE_FRAMES=1 ;;
        --print-config) PRINT_CONFIG=1 ;;
        --isolation=*) ISOLATION="${1#--isolation=}" ;;
        --isolation) shift; ISOLATION="${1:-}" ;;
        --suite) shift; SUITE_LIST_IN="${1:-}" ;;
        --suite=*) SUITE_LIST_IN="${1#--suite=}" ;;
        --mod) shift; MODS_UNDER_TEST+=("${1:-}") ;;
        --mod=*) MODS_UNDER_TEST+=("${1#--mod=}") ;;
        --mod-overlay) shift; MOD_OVERLAY_DIRS+=("${1:-}") ;;
        --mod-overlay=*) MOD_OVERLAY_DIRS+=("${1#--mod-overlay=}") ;;
        --install) shift; INSTALL_PAIRS+=("${1:-}") ;;
        --install=*) INSTALL_PAIRS+=("${1#--install=}") ;;
        --profiler) PROFILER=1 ;;
        --recover-only) RECOVER_ONLY=1 ;;
        -*) echo "[run_test] unknown flag: $1" >&2; usage ;;
        *) SCENARIOS+=("$1") ;;
    esac
    shift
done

case "$ISOLATION" in
    auto|always|never) ;;
    *) fail "--isolation must be auto, always or never (got '$ISOLATION')" ;;
esac

# Resolve each --mod to an absolute path plus the packageId read from its OWN About/About.xml. The
# ids are not hardcoded here on purpose: a list of them baked into this script is precisely the
# single-mod coupling this replaces, and it would silently rot the moment a mod renamed itself.
MOD_UT_DIRS=()
MOD_UT_LINKS=()
for mod_arg in ${MODS_UNDER_TEST[@]+"${MODS_UNDER_TEST[@]}"}; do
    [[ -n "$mod_arg" ]] || fail "--mod needs a path."
    [[ -d "$mod_arg" ]] || fail "--mod '$mod_arg' is not a directory."
    [[ -f "$mod_arg/About/About.xml" ]] ||
        fail "--mod '$mod_arg' has no About/About.xml — that's not a RimWorld mod folder."
    mod_dir="$(cd "$mod_arg" && pwd)"
    MOD_UT_DIRS+=("$mod_dir")
    MOD_UT_LINKS+=("$(basename "$mod_dir")")
done

# ---------------------------------------------------------------------------
# --mod-overlay resolution
# ---------------------------------------------------------------------------
# Reads one mod folder's packageId. The batch reader further down does more (it also works out which
# mods must load after the harness), but overlay resolution has to happen up here so a bad
# --mod-overlay fails before the run takes the lock rather than after — and so --print-config can
# show what would be installed where.
read_package_id() {
    python3 - "$1" <<'PYEOF'
import re, sys
with open(f"{sys.argv[1]}/About/About.xml", encoding="utf-8") as f:
    m = re.search(r"<packageId>(.*?)</packageId>", f.read(), re.S | re.I)
if not m:
    sys.exit(f"no <packageId> in {sys.argv[1]}/About/About.xml")
print(m.group(1).strip().lower())
PYEOF
}

# Find the mod folder the game will actually load for a packageId — the one an overlay has to land
# on. Checked in two places, in this order:
#   1. the --mod folders, because a run that names a mod is telling us which copy it means;
#   2. the Mods/ folder, for the (normal) case where the mod is permanently installed there and the
#      caller did not need to name it.
# Ambiguity is a hard failure rather than a first-match-wins, because picking wrong here means
# installing a branch build over the wrong copy and then "restoring" it onto that copy too.
#
# Note the shape of every test below: `if <condition>; then ...; fi`, never a trailing
# `[[ ... ]] && do_thing`. Under `set -e` a false `&&` test as the last command of a loop body makes
# the whole loop exit nonzero, which aborts the script (or, inside a $(...), silently returns an empty
# string). Same reason the house style prefers an inverted `if` over a bare `continue`.
matches_package_id() {
    local dir="$1" want_id="$2" found
    if [[ ! -f "$dir/About/About.xml" ]]; then
        return 1
    fi
    found="$(read_package_id "$dir" 2>/dev/null)" || return 1
    [[ "$found" == "$want_id" ]]
}

resolve_overlay_target() {
    local want_id="$1" exclude_dir="$2" candidate resolved
    local matches=()
    for candidate in ${MOD_UT_DIRS[@]+"${MOD_UT_DIRS[@]}"}; do
        if [[ "$candidate" != "$exclude_dir" ]] && matches_package_id "$candidate" "$want_id"; then
            matches+=("$candidate")
        fi
    done
    if (( ${#matches[@]} == 0 )); then
        for candidate in "$MODS_DIR"/*/; do
            candidate="${candidate%/}"
            resolved="$(readlink -f "$candidate")"
            if [[ "$resolved" != "$exclude_dir" ]] && matches_package_id "$candidate" "$want_id"; then
                matches+=("$resolved")
            fi
        done
    fi
    if (( ${#matches[@]} == 0 )); then
        fail "--mod-overlay: nothing installed with packageId '$want_id' to overlay onto. Pass the installed copy as --mod, or symlink it into $MODS_DIR."
    fi
    if (( ${#matches[@]} > 1 )); then
        fail "--mod-overlay: packageId '$want_id' resolves to several folders (${matches[*]}) — ambiguous, refusing to guess."
    fi
    printf '%s' "${matches[0]}"
}

# A worktree passed as BOTH --mod-overlay and --mod is the mistake the overlay flag exists to prevent:
# two folders sharing one packageId, which RimWorld resolves by a rule nobody should have to know.
for overlay_arg in ${MOD_OVERLAY_DIRS[@]+"${MOD_OVERLAY_DIRS[@]}"}; do
    [[ -n "$overlay_arg" ]] || fail "--mod-overlay needs a path."
    [[ -d "$overlay_arg" ]] || fail "--mod-overlay '$overlay_arg' is not a directory."
    [[ -f "$overlay_arg/About/About.xml" ]] ||
        fail "--mod-overlay '$overlay_arg' has no About/About.xml — that's not a RimWorld mod folder."
    overlay_dir="$(cd "$overlay_arg" && pwd)"
    for mod_dir in ${MOD_UT_DIRS[@]+"${MOD_UT_DIRS[@]}"}; do
        if [[ "$mod_dir" == "$overlay_dir" ]]; then
            fail "'$overlay_dir' is passed as both --mod and --mod-overlay. Use --mod-overlay alone: it installs this build over the already-installed copy, which is what makes the game load it."
        fi
    done
    overlay_src="$overlay_dir/1.6/Assemblies"
    [[ -d "$overlay_src" ]] ||
        fail "--mod-overlay '$overlay_dir' has no 1.6/Assemblies — run its build.sh first."
    overlay_id="$(read_package_id "$overlay_dir")" ||
        fail "--mod-overlay '$overlay_dir': could not read its packageId."
    overlay_dest="$(resolve_overlay_target "$overlay_id" "$overlay_dir")/1.6/Assemblies"
    INSTALL_PAIRS+=("$overlay_src:$overlay_dest")
    log "--mod-overlay: $overlay_id — $overlay_src -> $overlay_dest"
done

# --profiler: resolve Dubs Performance Analyzer's packageId from whatever copy is installed, rather
# than hardcoding it. The id in its About.xml is "Dubwise.DubsPerformanceAnalyzer.steam" (yes, with a
# literal ".steam"), and RimWorld additionally appends "_steam" to a Workshop mod only when a LOCAL
# copy of the same id also exists — so hardcoding one spelling would silently produce a ModsConfig
# entry naming no installed mod, and RimWorld would boot without the analyzer while the run's Profile
# steps reported "not loaded". Reading the id off the folder we found cannot drift.
#
# Prefers a local Mods/ copy: when both exist, RimWorld is the one that renames the WORKSHOP copy, so
# the local folder's id is the one that stays as written.
PROFILER_MOD_ID=""
resolve_profiler_mod() {
    local candidate resolved id
    local matches=()
    for candidate in "$MODS_DIR"/*/ "$WORKSHOP_DIR"/*/; do
        candidate="${candidate%/}"
        if [[ -f "$candidate/About/About.xml" ]]; then
            id="$(read_package_id "$candidate" 2>/dev/null)" || id=""
            if [[ "$id" == dubwise.dubsperformanceanalyzer* ]]; then
                resolved="$(readlink -f "$candidate")"
                matches+=("$id|$resolved")
            fi
        fi
    done
    if (( ${#matches[@]} == 0 )); then
        fail "--profiler: Dubs Performance Analyzer is not installed. Subscribe to Workshop item 2038874626 (or drop a copy in $MODS_DIR), or drop the flag."
    fi
    # First match wins here, unlike --mod-overlay's hard failure on ambiguity, because the two copies
    # of this mod are the SAME mod and either one profiles identically — there is no wrong choice to
    # protect against, and MODS_DIR is scanned first so the local copy is the one taken.
    PROFILER_MOD_ID="${matches[0]%%|*}"
    log "--profiler: Dubs Performance Analyzer '$PROFILER_MOD_ID' (${matches[0]#*|})"
    log "--profiler: WARNING — the analyzer instruments every Harmony patch in this load. Timings from"
    log "--profiler:           this run, probes included, are measured through an instrumented build"
    log "--profiler:           and are not comparable with a run that did not pass --profiler."
}

if [[ $PROFILER -eq 1 ]]; then
    resolve_profiler_mod
fi

# Validate every install pair (both forms land here) before anything is locked or touched. The ':'
# split is why neither path may contain one; saying so now beats a confusing "no such directory".
INSTALL_SRCS=()
INSTALL_DESTS=()
for pair in ${INSTALL_PAIRS[@]+"${INSTALL_PAIRS[@]}"}; do
    [[ "$pair" == *:* ]] || fail "--install expects <src-dir>:<dest-dir> (got '$pair')."
    install_src="${pair%%:*}"
    install_dest="${pair#*:}"
    if [[ "$install_dest" == *:* ]]; then
        fail "--install paths may not contain ':' (got '$pair')."
    fi
    [[ -d "$install_src" ]] || fail "--install source '$install_src' is not a directory."
    [[ -d "$install_dest" ]] || fail "--install destination '$install_dest' is not a directory."
    INSTALL_SRCS+=("$(cd "$install_src" && pwd)")
    INSTALL_DESTS+=("$(cd "$install_dest" && pwd)")
done

# A --suite list contributes its entries alongside any given on the command line, so the two ways of
# selecting scenarios compose instead of one silently winning.
#
# Same grammar as Shared/SuiteList.cs (blank lines and whole-line '#' comments skipped, relative paths
# resolved against the list file's directory) because this script re-emits the resolved set as the list
# it actually hands the game — so a --suite file is read here and the harness only ever parses one
# machine-generated list of absolute paths.
suite_list_entry() {
    local line="$1" trimmed
    trimmed="$(printf '%s' "${line%%$'\r'}" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    if [[ -n "$trimmed" && "$trimmed" != \#* ]]; then
        [[ "$trimmed" == /* ]] || trimmed="$SUITE_LIST_DIR/$trimmed"
        SCENARIOS+=("$trimmed")
    fi
}

if [[ -n "$SUITE_LIST_IN" ]]; then
    [[ -f "$SUITE_LIST_IN" ]] || fail "suite list not found: $SUITE_LIST_IN"
    SUITE_LIST_DIR="$(cd "$(dirname "$SUITE_LIST_IN")" && pwd)"
    while IFS= read -r line; do
        suite_list_entry "$line"
    done < "$SUITE_LIST_IN"
fi

# --recover-only is a repair tool, not a run: it takes the lock, rolls back whatever a dead run left
# claimed, and exits. It therefore needs no scenario, and refuses one so nobody expects it to also
# test something.
if (( RECOVER_ONLY )); then
    if (( ${#SCENARIOS[@]} )); then
        fail "--recover-only takes no scenarios — it only rolls back an abandoned claim ledger."
    fi
else
    (( ${#SCENARIOS[@]} )) || usage
fi

# Resolve to absolute paths up front: the suite list handed to the game must not depend on this
# script's cwd, and the report folder keeps a copy of it as a run artifact.
for i in "${!SCENARIOS[@]}"; do
    [[ -f "${SCENARIOS[$i]}" ]] || fail "scenario file not found: ${SCENARIOS[$i]}"
    SCENARIOS[$i]="$(abspath "${SCENARIOS[$i]}")"
done

# Suite mode is anything other than exactly one scenario given positionally with no --suite. Keyed
# off the invocation rather than the count so the single-scenario path — the fallback, and the one
# whose report shape everything else already reads — stays byte-for-byte what it was.
SUITE_MODE=1
if (( ${#SCENARIOS[@]} == 1 )) && [[ -z "$SUITE_LIST_IN" ]]; then
    SUITE_MODE=0
fi

# --print-config resolves every path this run would touch and exits without launching anything,
# creating anything, or taking the lock. That makes the isolation properties checkable offline: run it
# twice and confirm the per-run paths differ and the shared ones don't.
print_config() {
    echo "RUN_ID=$RUN_ID"
    echo "RUN_TMP_DIR=$RUN_TMP_DIR"
    echo "ISOLATE_SAVEDATA=$ISOLATE_SAVEDATA"
    echo "REAL_CONFIG_DIR=$REAL_CONFIG_DIR"
    echo "CONFIG_DIR=$CONFIG_DIR"
    echo "MODSCONFIG=$MODSCONFIG"
    echo "PREFS=$PREFS"
    echo "SAVES_DIR=$SAVES_DIR"
    echo "AUTOSTART_SAVE=$AUTOSTART_SAVE"
    echo "PLAYER_LOG=$PLAYER_LOG"
    echo "RIMWORLD_STDERR=$RIMWORLD_STDERR"
    echo "LOCK_FILE=$LOCK_FILE"
    echo "LEDGER_FILE=$LEDGER_FILE"
    echo "CLAIM_BACKUP_DIR=$CLAIM_BACKUP_DIR"
    # Printed as resolved src->dest pairs rather than as the flags that produced them: --mod-overlay's
    # whole job is working out the destination, and being able to check that answer without launching
    # a game is the point of --print-config.
    # Plain "${!arr[@]}" — index expansion is already empty-safe under `set -u`. (The
    # ${arr[@]+"${arr[@]}"} guard used elsewhere in this script is for *value* expansion, and writing
    # it as ${!arr[@]+...} is a parse error: bash reads that as indirect expansion of the array's
    # value, and dies with "invalid variable name" on a non-empty array.)
    for i in "${!INSTALL_SRCS[@]}"; do
        echo "INSTALL=${INSTALL_SRCS[$i]} -> ${INSTALL_DESTS[$i]}"
    done
    echo "REPORTS_DIR=$REPORTS_DIR"
    # The scenario LIST, not the single $SCENARIO: this function runs before the suite/single split
    # is resolved, so $SCENARIO does not exist yet and reading it here aborted the whole flag under
    # `set -u`. Printing the list is also the more useful answer once a run can hold several.
    for scenario_path in ${SCENARIOS[@]+"${SCENARIOS[@]}"}; do
        echo "SCENARIO=$scenario_path"
    done
    for mod_dir in ${MOD_UT_DIRS[@]+"${MOD_UT_DIRS[@]}"}; do
        echo "MOD_UNDER_TEST=$mod_dir"
    done
}
if [[ $PRINT_CONFIG -eq 1 ]]; then
    print_config
    exit 0
fi

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------
command -v jq >/dev/null || fail "jq not found on PATH."
command -v python3 >/dev/null || fail "python3 not found on PATH."
command -v flock >/dev/null || fail "flock not found on PATH (needed for the run guard)."
[[ -x "$RIMWORLD/RimWorldLinux" ]] || fail "RimWorldLinux not found at $RIMWORLD."

# GenCommandLine.TryGetCommandLineArg splits on '=' and requires exactly two halves, so a save-data
# path containing '=' would be silently ignored and the game would use the real config dir.
if [[ "$ISOLATE_SAVEDATA" == "1" && "$CONFIG_DIR" == *=* ]]; then
    fail "isolated save-data dir contains '=' ($CONFIG_DIR) — RimWorld's -savedatafolder= parser cannot express it."
fi

# ---------------------------------------------------------------------------
# Run guard
# ---------------------------------------------------------------------------
# Two reasons a run must refuse to start rather than barge in:
#   1. This box has ~4GiB free of 14GiB and one integrated GPU. Two rendering RimWorlds is at best
#      slow enough to trip the report timeout.
#   2. Even one *overlapping* run corrupts the other: the ModsConfig/autostart swap is global, and a
#      just-exited session's shutdown writes have already caused one confusing false failure.
# The lock is held on fd 9 for the life of the script, so the kernel releases it however we die — no
# stale-lock cleanup path to get wrong. The file is never deleted (unlinking it races with waiters).
# Both checks deliberately sit before the EXIT trap is armed: bowing out here must not run teardown,
# which would restore/remove config this run never touched.
exec 9>"$LOCK_FILE" || fail "cannot open lock file $LOCK_FILE"
if ! flock -n 9; then
    fail "another run_test.sh holds $LOCK_FILE — wait for it to finish (RWTH_LOCK_FILE overrides)."
fi

# Separate check: the lock only sees other runs of this script, not a RimWorld the user launched from
# Steam. We must not kill that (the old code did, via pkill), so we bow out instead.
#
# Zombies do not count as running. A RimWorld launched from RimSort (or any parent that doesn't reap
# its children promptly) leaves a <defunct> entry behind after the game has fully exited, and a bare
# `pgrep -x` matches it — which blocks every run until the *launcher* is closed, reporting that a game
# is still open when it isn't. A zombie holds no window, no GPU context and no config lock, so there is
# nothing here to protect: it is already dead, and cannot be killed anyway. This is the same
# alive-vs-matching distinction rimworld_alive() further down had to make for a different reason.
live_rimworld_pids() {
    local pid state
    for pid in $(pgrep -x RimWorldLinux 2>/dev/null); do
        # Empty state == it exited between pgrep and here; Z* == zombie. Neither one is alive.
        state=$(ps -o stat= -p "$pid" 2>/dev/null | tr -d '[:space:]')
        if [[ -n "$state" && "$state" != Z* ]]; then
            echo "$pid"
        fi
    done
}
LIVE_RIMWORLD="$(live_rimworld_pids | tr '\n' ' ')"
if [[ -n "${LIVE_RIMWORLD// /}" ]]; then
    fail "a RimWorldLinux process is already running (PID(s): ${LIVE_RIMWORLD%% }) — close it first. This run will not kill it (it may be your game, or another agent's run)."
fi

# ---------------------------------------------------------------------------
# Recover an abandoned claim ledger
# ---------------------------------------------------------------------------
# We hold the lock and no game is up, so a ledger sitting at $LEDGER_FILE cannot belong to anything
# live: some earlier run died before its teardown finished, and whatever it swapped in is still
# swapped in. Rolling that back BEFORE we take our own claims is what stops the damage compounding —
# the failure this fixes is a run backing up an already-replaced file, so that the only copy of the
# real one is in a scratch dir nobody will think to look in. That happened: an 828-mod ModsConfig sat
# replaced by a 14-mod test list for hours while every run in between passed.
#
# --guard is the whole reason this can be automatic. Each item is rolled back only if it still hashes
# to exactly what the dead run installed; anything edited since is left alone and printed. So the
# common case (nothing touched it — nobody else knew it was there) is repaired silently, and the
# ambiguous case is escalated instead of guessed at.
CLAIM_TOOL="$SCRIPT_DIR/asset_claims.py"
[[ -f "$CLAIM_TOOL" ]] || fail "$CLAIM_TOOL missing — the runner cannot record what it swaps without it."

recover_abandoned_ledger() {
    if [[ ! -f "$LEDGER_FILE" ]]; then
        return 0
    fi
    log "--- Step 0: an earlier run left claims behind — rolling them back ---"
    log "Abandoned ledger: $LEDGER_FILE"
    # Informational only, so a ledger this script cannot parse must not abort the run before the
    # restore below has had its go (`set -o pipefail` would otherwise make a nonzero status fatal).
    python3 "$CLAIM_TOOL" --ledger "$LEDGER_FILE" status 2>&1 | sed 's/^/[run_test]   /' || true
    if python3 "$CLAIM_TOOL" --ledger "$LEDGER_FILE" restore --guard --verbose \
            2>&1 | sed 's/^/[run_test]   /'; then
        log "Recovered — the machine is back to its pre-run state."
        return 0
    fi
    # Something could not be rolled back safely. The ledger is moved aside rather than kept, because
    # keeping it would block or re-warn on every future run forever; and rather than deleted, because
    # it names the backups that still hold the original content.
    local parked="$LEDGER_FILE.unrecovered-$RUN_ID"
    mv "$LEDGER_FILE" "$parked"
    log "Warning: some claims above could NOT be rolled back automatically (they were modified after"
    log "         that run installed them, so restoring would have overwritten someone's change)."
    log "         The ledger naming their backups is parked at: $parked"
    log "         Inspect with: python3 $CLAIM_TOOL --ledger $parked status"
}
recover_abandoned_ledger

if (( RECOVER_ONLY )); then
    log "--recover-only: nothing left to do."
    exit 0
fi

# One save source per run: it is installed once at boot as autostart.rws, and the mid-suite reload
# reloads that same file. Two scenarios naming different fixtures cannot both be honoured, so this
# refuses rather than running the second against the first's colony.
SAVE_FILE=""
for scenario in "${SCENARIOS[@]}"; do
    this_save="$(jq -r '.saveFile // ""' "$scenario")"
    if [[ -z "$SAVE_FILE" ]]; then
        SAVE_FILE="$this_save"
    elif [[ "$this_save" != "$SAVE_FILE" ]]; then
        fail "scenarios in one run must share a saveFile: '$SAVE_FILE' vs '$this_save' ($scenario). Split the run."
    fi
done

# Union, so a suite boots with every mod any of its scenarios needs.
REQUIRED_MODS_JSON="$(jq -s -c '[.[] | (.requiredMods // {}) | keys[]] | unique' "${SCENARIOS[@]}")"

SCENARIO="${SCENARIOS[0]}"
if (( SUITE_MODE )); then
    RUN_LABEL="suite of ${#SCENARIOS[@]} ($(jq -r '.name' "${SCENARIOS[@]}" | tr '\n' ' ' | sed 's/ $//'))"
else
    RUN_LABEL="$(jq -r '.name' "$SCENARIO")"
fi

# Save source: prefer a manually-created fixture (deterministic, reproducible colony) if one
# exists; otherwise fall back to RimWorld's own -quicktest, which generates a fresh vanilla
# Crashlanded colony (Cassandra/Rough, 250x250, random tile) at boot. Verse.Root_Play.Start()'s
# final else-branch calls SetupForQuickTestPlay() + InitNewGame() when there's no autostart save
# and no gameToLoad — see DESIGN.md. The fallback is fine for probes whose value depends only on
# scripted state (latitude/season/time), not on which colony/pawns exist — e.g. shadow_lean.
FIXTURE_SAVE="$REPO_DIR/Fixtures/$SAVE_FILE"
if [[ -f "$FIXTURE_SAVE" ]]; then
    USE_QUICKTEST=0
    log "Save source: fixture $FIXTURE_SAVE (loaded via vanilla autostart mechanism)."
else
    USE_QUICKTEST=1
    log "Save source: no fixture at $FIXTURE_SAVE — using -quicktest to generate a fresh colony."
fi

HARNESS_DLL="$REPO_DIR/1.6/Assemblies/RimWorldTestHarness.dll"
[[ -f "$HARNESS_DLL" ]] || fail "$HARNESS_DLL missing — run RimWorldTestHarness/build.sh first."

# A mod under test with no built assembly is WARNED about, not failed on: a Defs-only XML mod is a
# perfectly good test subject, and this script can't tell one from a C# mod someone forgot to build.
# The scenario's own probe steps are the real gate — a missing probe fails the run with a named
# error rather than passing quietly.
for mod_dir in ${MOD_UT_DIRS[@]+"${MOD_UT_DIRS[@]}"}; do
    compgen -G "$mod_dir/*/Assemblies/*.dll" >/dev/null ||
        log "Warning: no built assembly under $mod_dir — if it's a C# mod, run its build.sh first."
done

# packageIds come from each mod's own About.xml. Lowercased because that's what RimWorld writes into
# ModsConfig.xml and what this script has always emitted for its own ids.
MOD_UT_IDS_JSON="[]"
MOD_UT_AFTER_HARNESS_JSON="[]"
if (( ${#MOD_UT_DIRS[@]} )); then
    MOD_UT_SPLIT_JSON="$(python3 - ${MOD_UT_DIRS[@]+"${MOD_UT_DIRS[@]}"} <<'PYEOF'
import json, re, sys

HARNESS = "joof.rimworldtestharness"

ids, after = [], []
for d in sys.argv[1:]:
    with open(f"{d}/About/About.xml", encoding="utf-8") as f:
        about = f.read()
    m = re.search(r"<packageId>(.*?)</packageId>", about, re.S | re.I)
    if not m:
        sys.exit(f"no <packageId> in {d}/About/About.xml")
    pid = m.group(1).strip().lower()
    ids.append(pid)
    # A probe-bridge mod IMPLEMENTS harness interfaces (IProbe, IStepAction), so its assembly cannot
    # even be type-scanned until RimWorldTestHarness.dll is loaded — RimWorld loads mod assemblies in
    # activeMods order, so a bridge listed before the harness dies with a ReflectionTypeLoadException
    # ("could not resolve ... IProbe") and every one of its probes silently goes unregistered.
    # A mod declares that need itself, in the About.xml it already ships, so detect it rather than
    # hardcoding which mod is a bridge — that was the coupling `--mod` set out to remove.
    if re.search(rf"<(?:packageId|li)>\s*{re.escape(HARNESS)}\s*</(?:packageId|li)>", about, re.I):
        after.append(pid)
print(json.dumps({"ids": ids, "after": after}))
PYEOF
)" || fail "could not read a packageId from a --mod About/About.xml"
    MOD_UT_IDS_JSON="$(echo "$MOD_UT_SPLIT_JSON" | jq -c '.ids')"
    MOD_UT_AFTER_HARNESS_JSON="$(echo "$MOD_UT_SPLIT_JSON" | jq -c '.after')"
    log "Mods under test: $(echo "$MOD_UT_IDS_JSON" | jq -r 'join(", ")')"
    if [[ "$(echo "$MOD_UT_AFTER_HARNESS_JSON" | jq -r 'length')" != "0" ]]; then
        log "  loading after the harness (they depend on it): $(echo "$MOD_UT_AFTER_HARNESS_JSON" | jq -r 'join(", ")')"
    fi
else
    log "Mods under test: none (harness-only run)."
fi

mkdir -p "$REPORTS_DIR"
# 700: this dir holds a copy of the user's ModsConfig/Prefs and lives in a world-readable /tmp.
mkdir -p -m 700 "$RUN_TMP_DIR"

RUN_STAMP="$(date +%Y%m%d-%H%M%S)"
if (( SUITE_MODE )); then
    REPORT_PATH="$REPORTS_DIR/suite-$RUN_STAMP.json"
    # The generated list lives beside the report as a run artifact: a suite whose membership can't be
    # reconstructed afterwards is a suite whose green result means less than it looks like.
    SUITE_LIST="$REPORTS_DIR/suite-$RUN_STAMP.txt"
    printf '%s\n' "${SCENARIOS[@]}" > "$SUITE_LIST"
else
    REPORT_PATH="$REPORTS_DIR/$(basename "$SCENARIO" .json)-$RUN_STAMP.json"
    SUITE_LIST=""
fi

# The save the driver reloads between scenarios that mutated the map. Set only in fixture mode —
# -quicktest generates its colony at boot and writes no save, so there is nothing to reload, and
# leaving this unset is what makes SuitePlanner refuse to pretend such a suite was isolated.
if (( USE_QUICKTEST )); then
    RELOAD_SAVE=""
else
    RELOAD_SAVE="autostart"
fi

log "Run: $RUN_LABEL (save=$SAVE_FILE)"
if (( SUITE_MODE )); then
    log "Suite mode: ${#SCENARIOS[@]} scenario(s) in one game load, isolation=$ISOLATION, reload save=${RELOAD_SAVE:-(none)}"
    log "Suite list: $SUITE_LIST"
fi
log "Report will be written to: $REPORT_PATH"
log "Run scratch dir: $RUN_TMP_DIR (backups, Player.log, stderr)"

# ---------------------------------------------------------------------------
# Save-data isolation (opt-in)
# ---------------------------------------------------------------------------
# Seed the isolated root with a copy of the real Config/ only. That folder is a few KB and carries the
# things RimWorld would otherwise re-derive from scratch — Prefs.xml, KeyPrefs.xml,
# LastPlayedVersion.txt, and the ModsConfig.xml whose <version>/<knownExpansions> Step 3 reads. Saves/
# is deliberately NOT copied: the only save this run needs is the fixture it installs itself.
seed_isolated_savedata() {
    log "--- Step 0: isolated save-data root (RWTH_ISOLATE_SAVEDATA=1) ---"
    [[ -d "$REAL_CONFIG_DIR/Config" ]] || fail "cannot seed isolation: $REAL_CONFIG_DIR/Config not found."
    mkdir -p "$CONFIG_DIR"
    cp -r "$REAL_CONFIG_DIR/Config" "$CONFIG_DIR/Config"
    mkdir -p "$SAVES_DIR"
    log "Seeded $CONFIG_DIR from $REAL_CONFIG_DIR/Config — the real save data is untouched this run."
}
[[ "$ISOLATE_SAVEDATA" == "1" ]] && seed_isolated_savedata

# ---------------------------------------------------------------------------
# Cleanup / teardown
# ---------------------------------------------------------------------------
# Everything this run swaps is recorded in the ledger before it is swapped, so teardown is a single
# rollback of that record rather than a hand-maintained list of undo steps.
#
# This replaces three separate bits of bookkeeping — CREATED_LINKS, AUTOSTART_BAK_MADE and
# BACKUPS_TAKEN — that each tracked one mutation and had to be kept in step with the code that made
# it. The bug that motivates a single record is the one BACKUPS_TAKEN was itself a patch for: reaching
# the restore path having taken no backup, where "no backup" was indistinguishable from "no prior file
# existed", so teardown deleted the user's real save. A claim states the prior state explicitly
# (absent / file / symlink), so there is no gap to misread.
#
# LEDGER_OPEN gates teardown the way BACKUPS_TAKEN used to gate the restore: the EXIT trap is armed
# before any claim is taken, and a rollback of claims that were never recorded must be a no-op.
LEDGER_OPEN=0

# Thin wrappers so call sites read as intent, and so every claim path goes through one place that
# knows to abort the run when a claim cannot be recorded. An unrecorded mutation is strictly worse
# than no run: it is the leftover a later run silently inherits.
claim_path_for_write() {   # claim DEST, which we are about to write ourselves; seal it afterwards
    python3 "$CLAIM_TOOL" --ledger "$LEDGER_FILE" claim --dest "$1" ||
        fail "could not record a claim on $1 — refusing to modify it unrecorded."
}
seal_claim() {
    python3 "$CLAIM_TOOL" --ledger "$LEDGER_FILE" seal --dest "$1" ||
        fail "could not seal the claim on $1."
}
claim_symlink_at() {       # point $1 at $2 for this run, recording whatever was there
    python3 "$CLAIM_TOOL" --ledger "$LEDGER_FILE" symlink --path "$1" --target "$2" |
        sed 's/^/[run_test] /' ||
        fail "could not claim the mod symlink $1."
}

# ----- process control (PID-scoped, never by name) -----
RIMWORLD_PID=""

# Liveness by /proc state rather than `kill -0`. `kill -0` also succeeds for a zombie — an exited child
# whose status hasn't been collected — which would make the wait-for-death loops below spin. In
# practice bash's SIGCHLD handler reaps background jobs within milliseconds so a zombie is unlikely to
# be observed here, but the loops must not depend on that timing. comm can contain spaces, hence the
# split on the last ')' rather than awk $3.
pid_alive() {
    local pid="$1" stat rest
    [[ -n "$pid" ]] || return 1
    [[ -r "/proc/$pid/stat" ]] || return 1
    stat="$(< "/proc/$pid/stat")" 2>/dev/null || return 1
    rest="${stat##*) }"
    [[ "${rest%% *}" != "Z" ]]
}

# Replaces `pkill -9 -x RimWorldLinux`, which killed every instance on the box — including the user's
# own game and any concurrent run. We only ever signal the child this script started.
#
# The old pkill -9 was relied on to guarantee teardown, so this must be just as final: SIGTERM first
# (lets Unity flush its log), then SIGKILL if it hasn't gone within the grace window. If even SIGKILL
# leaves it there (uninterruptible in a GPU/driver call), say so loudly instead of pretending.
kill_rimworld() {
    local pid="${RIMWORLD_PID:-}" waited=0
    pid_alive "$pid" || return 0

    log "Stopping RimWorld (PID $pid) with SIGTERM..."
    kill -TERM "$pid" 2>/dev/null || true
    while pid_alive "$pid" && (( waited < 10 )); do
        sleep 1
        waited=$((waited + 1))
    done

    if pid_alive "$pid"; then
        log "PID $pid ignored SIGTERM after ${waited}s — escalating to SIGKILL."
        kill -KILL "$pid" 2>/dev/null || true
        waited=0
        while pid_alive "$pid" && (( waited < 10 )); do
            sleep 1
            waited=$((waited + 1))
        done
    fi

    if pid_alive "$pid"; then
        log "Warning: PID $pid survived SIGKILL for 10s — it may be stuck in the kernel. Not escalating by name."
    else
        log "RimWorld PID $pid stopped."
        wait "$pid" 2>/dev/null || true   # reap the zombie so /proc/<pid> goes away
    fi
}

cleanup_done=0
cleanup() {
    if [[ $cleanup_done -eq 1 ]]; then return; fi
    cleanup_done=1
    log "Cleaning up..."
    kill_rimworld
    if [[ $NO_TEARDOWN -eq 0 ]]; then
        teardown
        # Only once teardown has actually put everything back. If it could not, the ledger is still
        # open and its claims still name backups in $CLAIM_BACKUP_DIR — discarding them here would
        # destroy the only copy of the content the next run (or a human) needs to finish the rollback,
        # which is the same "the backup was the last copy" failure the ledger exists to prevent.
        if [[ $LEDGER_OPEN -eq 0 ]]; then
            discard_run_scratch
        else
            log "Keeping $CLAIM_BACKUP_DIR — the claims above still need it."
        fi
    else
        # The ledger deliberately survives --no-teardown: it is the record of what is still swapped
        # in, and the next run rolls it back (hash-guarded) rather than inheriting it blindly.
        log "--no-teardown: leaving symlinks, ModsConfig, Saves/autostart.rws, and $RUN_TMP_DIR in place."
        log "              Claims remain recorded in $LEDGER_FILE — roll them back with:"
        log "              $SCRIPT_DIR/run_test.sh --recover-only"
    fi
}
trap cleanup EXIT INT TERM

# The config backups have done their job once teardown has restored them; leaving copies of the user's
# ModsConfig/Prefs lying around in /tmp is pointless. Player.log and the stderr log stay — they're the
# post-mortem evidence every failure message points at — so the dir is only removed if it ends up
# empty, i.e. on a clean run with nothing worth keeping.
# Rewrites <devMode> to True in place, inserting the element if Prefs.xml doesn't carry one (RimWorld
# omits elements left at their default). Done in python3 — already a hard dependency of this script —
# rather than sed, because the insert case needs to find the root element's closing tag, and a regex
# that silently matched nothing would hand back the exact failure this whole function exists to fix.
# The run is aborted rather than continued if the result doesn't read back as true: booting anyway
# means a scenario that either times out or measures a hand-loaded map, which is far worse than a stop.
seed_devmode() {
    python3 - "$PREFS" <<'PY' || fail "could not seed <devMode>True</devMode> into Prefs.xml"
import re, sys

path = sys.argv[1]
with open(path, encoding="utf-8") as f:
    text = f.read()

new, n = re.subn(r"<devMode>\s*\w*\s*</devMode>", "<devMode>True</devMode>", text, count=1)
if n == 0:
    # No <devMode> element at all: insert one just inside the root's closing tag.
    m = re.search(r"\n(\s*)</\w+>\s*$", new)
    if not m:
        sys.exit("Prefs.xml has no recognisable root closing tag")
    new = new[:m.start()] + f"\n{m.group(1)}  <devMode>True</devMode>" + new[m.start():]

with open(path, "w", encoding="utf-8") as f:
    f.write(new)

if "<devMode>True</devMode>" not in open(path, encoding="utf-8").read():
    sys.exit("devMode did not read back as True after the write")
PY
    log "Seeded <devMode>True</devMode> into Prefs.xml (restored on teardown)."
}

discard_run_scratch() {
    rm -rf "$CLAIM_BACKUP_DIR"
    [[ "$ISOLATE_SAVEDATA" == "1" ]] && rm -rf "$CONFIG_DIR"
    rmdir "$RUN_TMP_DIR" 2>/dev/null || log "Logs kept in $RUN_TMP_DIR"
}

# Teardown is now one operation: roll the ledger back, newest claim first.
#
# Deliberately UNGUARDED, unlike the recovery path at Step 0. The guard asks "is this still exactly
# what we installed?", and here the answer is legitimately no for two of the claims: RimWorld rewrites
# the whole of Prefs.xml from memory as it exits, and the game may have touched the save. This run owns
# those files — it recorded what was there before it took them — so it restores unconditionally. The
# guard exists for the case where we are undoing a run that is no longer around to vouch for itself.
#
# This runs after kill_rimworld has waited on the game, so the game's own final write cannot land on
# top of the restore.
teardown() {
    if [[ $LEDGER_OPEN -eq 0 ]]; then
        log "Nothing to restore — the run never claimed anything."
        return 0
    fi
    log "Rolling back every claim (config, save, mod symlinks, installed assemblies)..."
    # Never fatal: a teardown that aborted partway would leave more global state swapped than one that
    # pushed on and reported. asset_claims.py prints each item it could not put back, and exits
    # nonzero, which we surface rather than propagate.
    if python3 "$CLAIM_TOOL" --ledger "$LEDGER_FILE" restore --verbose 2>&1 | sed 's/^/[run_test]   /'; then
        log "All claims rolled back; ledger closed."
        LEDGER_OPEN=0
    else
        log "Warning: some claims could not be rolled back — see above. Ledger kept at $LEDGER_FILE;"
        log "         the next run will try again (hash-guarded), or use --recover-only now."
    fi
}

# ---------------------------------------------------------------------------
# Step 1: claim the mod symlinks
# ---------------------------------------------------------------------------
# Every mod folder the run needs gets a claimed symlink: the previous state is recorded, the link is
# pointed at the folder we were given, and teardown puts it back.
#
# The old rule was "if something is already there, leave it alone", which looked like the careful
# choice and was not. Every worktree's probe-bridge mod has the same basename and packageId
# (`TestMod`), so a link left behind by a crashed run on another branch silently won over the --mod
# this run was handed — and the failure shows up as a *selective* missing probe (anything added after
# that branch), which reads like a bug in the mod's own registration code and sends you looking in
# entirely the wrong file. Repointing is safe because we hold the run lock: no other run exists to
# disturb, and the prior target is recorded either way.
#
# An already-correct link is still claimed. It costs nothing, its rollback is a no-op, and it makes
# the ledger a complete statement of what the run loaded — which is what you want to read afterwards
# when a result is confusing.
log "--- Step 1: mod symlinks ---"
python3 "$CLAIM_TOOL" --ledger "$LEDGER_FILE" begin --run-id "$RUN_ID" --backup-dir "$CLAIM_BACKUP_DIR" ||
    fail "could not open the claim ledger at $LEDGER_FILE."
LEDGER_OPEN=1
log "Claim ledger: $LEDGER_FILE (backups in $CLAIM_BACKUP_DIR)"

claim_symlink_at "$MODS_DIR/RimWorldTestHarness" "$REPO_DIR"
log "Mods/RimWorldTestHarness -> $REPO_DIR"
for i in "${!MOD_UT_DIRS[@]}"; do
    claim_symlink_at "$MODS_DIR/${MOD_UT_LINKS[$i]}" "${MOD_UT_DIRS[$i]}"
    log "Mods/${MOD_UT_LINKS[$i]} -> ${MOD_UT_DIRS[$i]}"
done

# ---------------------------------------------------------------------------
# Step 2: claim ModsConfig.xml, Prefs.xml and any existing autostart.rws
# ---------------------------------------------------------------------------
# Claimed rather than copied to a .bak of our own: same backup, but recorded in the ledger, so a run
# that dies before teardown leaves a machine-readable statement of what it swapped instead of a
# scratch directory nobody will think to search. That distinction is the whole outage — the real
# 828-mod ModsConfig had been sitting in an old /tmp/rwth-run-*/ for hours while every run in between
# happily backed up the test list that had replaced it.
log "--- Step 2: claiming the files this run swaps ---"
[[ -f "$MODSCONFIG" ]] || fail "ModsConfig.xml not found at $MODSCONFIG — has RimWorld been run at least once?"
claim_path_for_write "$MODSCONFIG"
log "Claimed ModsConfig.xml"

[[ -f "$PREFS" ]] || fail "Prefs.xml not found at $PREFS — has RimWorld been run at least once?"
claim_path_for_write "$PREFS"
log "Claimed Prefs.xml"
seed_devmode
seal_claim "$PREFS"

mkdir -p "$SAVES_DIR"
# Claimed whether or not it exists. "Absent" is a recorded prior state, not the absence of a record —
# which is the distinction that stops teardown deleting a real save it never backed up.
claim_path_for_write "$AUTOSTART_SAVE"
log "Claimed Saves/autostart.rws (present before this run: $([[ -f "$AUTOSTART_SAVE" ]] && echo yes || echo no))"

# ---------------------------------------------------------------------------
# Step 3: write minimal ModsConfig.xml
# ---------------------------------------------------------------------------
log "--- Step 3: writing minimal ModsConfig.xml ---"
EXCLUDED_DLC_JSON="$(printf '%s\n' ${EXCLUDED_DLC[@]+"${EXCLUDED_DLC[@]}"} | python3 -c \
    'import json,sys; print(json.dumps([l for l in sys.stdin.read().split("\n") if l]))')"
RIMWORLD="$RIMWORLD" REQUIRED_MODS_JSON="$REQUIRED_MODS_JSON" \
    MOD_UT_IDS_JSON="$MOD_UT_IDS_JSON" MOD_UT_AFTER_HARNESS_JSON="$MOD_UT_AFTER_HARNESS_JSON" \
    EXCLUDED_DLC_JSON="$EXCLUDED_DLC_JSON" PROFILER_MOD_ID="$PROFILER_MOD_ID" \
python3 - "$MODSCONFIG" <<'PYEOF'
import json, os, re, sys

path = sys.argv[1]
orig = open(path, encoding="utf-8").read()
m = re.search(r"<version>(.*?)</version>", orig, re.S)
version = m.group(1).strip() if m else ""
m = re.search(r"<knownExpansions>.*?</knownExpansions>", orig, re.S)
known = m.group(0) if m else "<knownExpansions />"

# Only include a DLC's packageId if it's actually installed (Data/<Folder> exists) — an
# uninstalled DLC in activeMods makes RimWorld refuse to boot.
dlc_candidates = [
    ("Royalty",  "ludeon.rimworld.royalty"),
    ("Ideology", "ludeon.rimworld.ideology"),
    ("Biotech",  "ludeon.rimworld.biotech"),
    ("Anomaly",  "ludeon.rimworld.anomaly"),
    ("Odyssey",  "ludeon.rimworld.odyssey"),
]
data_dir = os.path.join(os.environ["RIMWORLD"], "Data")
# --without-dlc deactivates an installed DLC rather than pretending it isn't on disk, because
# ModsConfig is what ModsConfig.<Dlc>Active reads. Reported out loud below: a run that quietly
# dropped an expansion would explain a scenario's every result wrongly.
excluded = set(json.loads(os.environ.get("EXCLUDED_DLC_JSON", "[]")))
dlc_ids = [pid for (folder, pid) in dlc_candidates
           if os.path.isdir(os.path.join(data_dir, folder)) and pid not in excluded]
for pid in sorted(excluded):
    print(f"[run_test]   --without-dlc: {pid} left INACTIVE for this run")

required = json.loads(os.environ.get("REQUIRED_MODS_JSON", "[]"))
mods_under_test = json.loads(os.environ.get("MOD_UT_IDS_JSON", "[]"))
after_harness = set(json.loads(os.environ.get("MOD_UT_AFTER_HARNESS_JSON", "[]")))

# The harness goes after the mods under test, for the reason About.xml's <loadAfter> states: its
# patches must wrap theirs and its probes must read state those mods have already applied. Mods under
# test keep the order they were passed in, so a mod plus its probe-bridge assembly land in the order
# their author asked for rather than one this script invented.
#
# The exception is a mod that declares a dependency ON the harness — a probe bridge. It implements
# harness interfaces, so RimWorld cannot type-scan its assembly until RimWorldTestHarness.dll is
# loaded, and activeMods order IS assembly load order. Listing such a mod before the harness costs
# every probe it registers: the whole assembly throws ReflectionTypeLoadException at load and the
# scenario then fails with "No probe registered named ...", pointing nowhere near the real cause.
# Note this is not in tension with the paragraph above — assembly LOAD order and Harmony PATCH order
# are different things, and a bridge registers probes rather than patching anything.
# --profiler goes immediately after Harmony and before everything else. The analyzer patches the
# Harmony CONSTRUCTOR to record which mod id owns which patch (Analyzer.Profiling.RememberHarmonyIDs),
# so any mod that constructs its Harmony instance before the analyzer loads has its patches attributed
# to nobody — the per-mod table this whole feature produces would come back missing exactly the rows
# the run was launched to look at. Empty string when --profiler was not passed, and filtered out below.
profiler = [p for p in [os.environ.get("PROFILER_MOD_ID", "")] if p]

active = []
for pid in (["ludeon.rimworld"] + dlc_ids + ["brrainz.harmony"] + profiler + required +
            [p for p in mods_under_test if p not in after_harness] +
            ["joof.rimworldtestharness"] +
            [p for p in mods_under_test if p in after_harness]):
    if pid not in active:
        active.append(pid)

lis = "\n".join(f"    <li>{pid}</li>" for pid in active)
out = (
    '<?xml version="1.0" encoding="utf-8"?>\n'
    "<ModsConfigData>\n"
    f"  <version>{version}</version>\n"
    f"  <activeMods>\n{lis}\n  </activeMods>\n"
    f"  {known}\n"
    "</ModsConfigData>\n"
)
open(path, "w", encoding="utf-8").write(out)
print(f"[run_test]   minimal modlist written: {len(active)} mods")
for pid in active:
    print(f"[run_test]     <li>{pid}</li>")
PYEOF

# Record the hash of the list we just generated. This is what lets a LATER run tell "the test list is
# still exactly as we left it, so restoring the real one is safe" from "someone has edited this since"
# — and it is the specific check that would have caught the real 828-mod list being replaced.
seal_claim "$MODSCONFIG"

# ---------------------------------------------------------------------------
# Step 4: install the save source
#   - Fixture mode: copy the fixture in as autostart.rws so vanilla autostarts it.
#   - Quicktest mode: ensure NO autostart.rws is present (it was backed up in Step 2), so
#     Root_Play.Start() skips the autostart/gameToLoad branches and falls to the generate-a-fresh-
#     colony else-branch. Launch adds -quicktest (Step 5) to jump straight into the Play scene.
# ---------------------------------------------------------------------------
if [[ $USE_QUICKTEST -eq 0 ]]; then
    log "--- Step 4: installing fixture save as autostart.rws ---"
    cp "$FIXTURE_SAVE" "$AUTOSTART_SAVE"
    log "Copied $FIXTURE_SAVE -> $AUTOSTART_SAVE"
else
    log "--- Step 4: quicktest mode — clearing autostart.rws so a fresh colony is generated ---"
    rm -f "$AUTOSTART_SAVE"
    log "Removed any autostart.rws (claimed in Step 2, restored on teardown)."
fi
seal_claim "$AUTOSTART_SAVE"

# ---------------------------------------------------------------------------
# Step 4b: install branch builds over their installed counterparts
# ---------------------------------------------------------------------------
# The step this whole mechanism exists for. `Mods/<Mod>` is a permanent symlink to a mod's main
# checkout, and Step 1 will not repoint it at a worktree without being told to (nor should it — two
# folders sharing a packageId is its own problem), so live-testing a branch means the branch's build
# output has to sit at the path the game already resolves to.
#
# Callers used to do that copy themselves, before invoking this script. That is the race: the copy
# happened outside the lock, so an agent could install its assembly, block on the lock, and have
# another agent's game boot against it — with the frames from that run reported as evidence for a fix
# that was never loaded. Doing it here means install, run and restore are all inside the same lock,
# and the restore is recorded rather than remembered.
install_claimed_overlays() {
    if (( ${#INSTALL_SRCS[@]} == 0 )); then
        return 0
    fi
    log "--- Step 4b: installing ${#INSTALL_SRCS[@]} build overlay(s) ---"
    local i
    for i in "${!INSTALL_SRCS[@]}"; do
        log "${INSTALL_SRCS[$i]} -> ${INSTALL_DESTS[$i]}"
        python3 "$CLAIM_TOOL" --ledger "$LEDGER_FILE" overlay \
            --src "${INSTALL_SRCS[$i]}" --dest "${INSTALL_DESTS[$i]}" 2>&1 | sed 's/^/[run_test] /' ||
            fail "could not install ${INSTALL_SRCS[$i]} over ${INSTALL_DESTS[$i]}."
    done
}
install_claimed_overlays

# ---------------------------------------------------------------------------
# Step 4c: fingerprint what will actually load
# ---------------------------------------------------------------------------
# Print the resolved target and content hash of every assembly the run is about to load. Nothing here
# changes behaviour; it exists because "which build did that run actually measure?" has been an
# unanswerable question after the fact more than once, and the answer is cheap to write down at the
# only moment it is knowable. Read it back off the run log when a result looks wrong.
log "--- Step 4c: what will load ---"
fingerprint_assemblies() {
    local mod_link resolved dll
    for mod_link in "$MODS_DIR/RimWorldTestHarness" ${MOD_UT_LINKS[@]+"${MOD_UT_LINKS[@]/#/$MODS_DIR/}"}; do
        resolved="$(readlink -f "$mod_link" 2>/dev/null || echo '?')"
        log "  $(basename "$mod_link") -> $resolved"
        while IFS= read -r dll; do
            # `|| echo ?` so an unreadable assembly costs a fingerprint, not the run: this whole
            # block is evidence-gathering, and under `set -e` a failed substitution inside `log`
            # would abort a run that was otherwise fine.
            log "    $(md5sum "$dll" 2>/dev/null | cut -c1-12 || echo '?')  ${dll#"$resolved"/}"
        done < <(find "$resolved" -path '*/Assemblies/*.dll' -type f 2>/dev/null | sort)
    done
}
fingerprint_assemblies

# A probe bridge built against a different tree than the build we just installed is the failure that
# looks like nothing: the bridge's assembly references a type the installed DLL doesn't have, the
# whole assembly throws ReflectionTypeLoadException at load, every probe and feature it registers
# silently goes missing — and `Screenshot` steps keep working, so the run produces a full set of
# plausible frames. Warn rather than refuse: a bridge that shares no types with the mod is legitimate,
# and we cannot tell the two apart without reading assembly references.
#
# The "is this overlay coherent?" test is a named predicate rather than a `return` out of the middle
# of the loop below, because returning from the function on the first coherent overlay would silently
# skip checking every later one.
some_mod_lives_under() {
    local root="$1" bridge
    for bridge in ${MOD_UT_DIRS[@]+"${MOD_UT_DIRS[@]}"}; do
        if [[ "$bridge" == "$root"/* ]]; then
            return 0
        fi
    done
    return 1
}

warn_on_split_build() {
    local overlay_src overlay_root
    for overlay_src in ${INSTALL_SRCS[@]+"${INSTALL_SRCS[@]}"}; do
        overlay_root="${overlay_src%/1.6/Assemblies}"
        if some_mod_lives_under "$overlay_root"; then
            continue   # a --mod folder comes from the overlaid tree, so they share a build
        fi
        log "  Warning: no --mod folder lives inside $overlay_root, whose build is being installed."
        log "           If one of them is that mod's probe bridge, it was built against a different"
        log "           tree, and a type it references may be missing from the DLL just installed —"
        log "           which unregisters every probe at load while screenshots still succeed."
    done
}
warn_on_split_build

# ---------------------------------------------------------------------------
# Step 5: launch, with retry on the known early-startup Boehm-GC crash
# ---------------------------------------------------------------------------
# "Is OUR game still up?" — not "is any RimWorld up?". The old pgrep -x version would report alive
# because a concurrent run's instance existed, so this run would sit waiting for a report that nothing
# was going to write. RimWorldLinux is a plain ELF launched directly here (no wrapper script that
# would exec a different PID), so $! is the process to watch.
rimworld_alive() {
    pid_alive "${RIMWORLD_PID:-}"
}

# With RWTH_ISOLATE_SAVEDATA=1 we must not merely hope the override took: a run that quietly used the
# real config dir while claiming isolation is worse than no isolation. GenFilePaths logs the override
# verbatim when it first resolves SaveDataFolderPath (well before mod load), so its absence — or a
# different path — is a hard failure.
assert_savedata_folder() {
    [[ "$ISOLATE_SAVEDATA" == "1" ]] || return 0
    local expected="Save data folder overridden to $CONFIG_DIR"
    if grep -qF "$expected" "$PLAYER_LOG" 2>/dev/null; then
        log "Verified: game reported '$expected'."
        return 0
    fi
    log "Player.log 'Save data folder' lines: $(grep -F 'Save data folder' "$PLAYER_LOG" 2>/dev/null || echo '(none)')"
    fail "isolation not confirmed — RimWorld never logged '$expected', so it may have used the real config dir. Check $PLAYER_LOG."
}

launch_rimworld() {
    local max_retries=5
    local attempt=0

    while true; do
        attempt=$((attempt + 1))
        log "Launching RimWorld (attempt $attempt / $max_retries)..."

        # Only ever our own previous attempt: the run-guard preflight already established no other
        # instance existed, and if one appeared since it isn't ours to kill.
        kill_rimworld

        : > "$PLAYER_LOG"
        : > "$RIMWORLD_STDERR"
        rm -f "$REPORT_PATH"

        # -quicktest (quicktest mode only): jumps straight from the entry scene into the Play
        # scene, where Root_Play.Start() generates a fresh colony (see Step 4). Harmless to omit
        # in fixture mode, where autostart.rws drives the load instead.
        local quicktest_arg=()
        [[ $USE_QUICKTEST -eq 1 ]] && quicktest_arg=(-quicktest)

        # RimWorld's own save-data redirect (see ISOLATE_SAVEDATA in Paths). Empty in the default
        # non-isolated mode, so the launch line is byte-identical to before.
        local savedata_arg=()
        [[ "$ISOLATE_SAVEDATA" == "1" ]] && savedata_arg=("-savedatafolder=$CONFIG_DIR")

        # Exactly one of RWTH_SCENARIO / RWTH_SUITE is set. HarnessMod keys the report shape off which
        # one it sees, so a single-scenario run keeps writing the bare ScenarioReport that Step 7 and
        # every other consumer of Runner/reports/*.json already understand.
        local harness_env=()
        if (( SUITE_MODE )); then
            harness_env=(RWTH_SUITE="$SUITE_LIST" RWTH_ISOLATION="$ISOLATION")
            [[ -n "$RELOAD_SAVE" ]] && harness_env+=(RWTH_RELOAD_SAVE="$RELOAD_SAVE")
        else
            harness_env=(RWTH_SCENARIO="$SCENARIO")
        fi

        env "${harness_env[@]}" RWTH_REPORT="$REPORT_PATH" \
            "$RIMWORLD/RimWorldLinux" --no-sandbox \
            "${quicktest_arg[@]}" \
            "${savedata_arg[@]}" \
            -logfile "$PLAYER_LOG" \
            2>"$RIMWORLD_STDERR" 9>&- &
        # 9>&- closes the run lock in the child: flock lives on the open file description, which a
        # forked game would otherwise keep held past our exit and lock out every later run.
        RIMWORLD_PID=$!
        log "RimWorldLinux PID: $RIMWORLD_PID"

        local waited=0
        while (( waited < 60 )); do
            if ! rimworld_alive; then
                break
            fi
            if grep -q "RWTH: harness loaded" "$PLAYER_LOG" 2>/dev/null; then
                break
            fi
            sleep 5
            waited=$((waited + 5))
        done

        if ! rimworld_alive; then
            if ! grep -q "RWTH: harness loaded" "$PLAYER_LOG" 2>/dev/null; then
                local sig=""
                if grep -q "Caught fatal signal\|GC_mark_from" "$PLAYER_LOG" "$RIMWORLD_STDERR" 2>/dev/null; then
                    sig=" (signature matched)"
                fi
                log "Died before mod load (attempt $attempt) — treating as flaky early-startup crash${sig}. Retrying..."
                if [[ $attempt -ge $max_retries ]]; then
                    fail "RimWorld died before mod load $max_retries times in a row. Check $PLAYER_LOG / $RIMWORLD_STDERR."
                fi
                continue
            else
                fail "RimWorldLinux exited AFTER mod load (PID $RIMWORLD_PID) — real crash. Check $PLAYER_LOG."
            fi
        fi

        log "RimWorld running (survived 60s crash window)."
        assert_savedata_folder
        return 0
    done
}
log "--- Step 5: launching RimWorld ---"
launch_rimworld

# ---------------------------------------------------------------------------
# Step 6: wait for the report
# ---------------------------------------------------------------------------
log "--- Step 6: waiting for scenario report ---"
elapsed=0
timeout_secs=900
while [[ ! -f "$REPORT_PATH" ]]; do
    if ! rimworld_alive; then
        fail "RimWorldLinux exited before writing a report. Check $PLAYER_LOG."
    fi
    sleep 5
    elapsed=$((elapsed + 5))
    if (( elapsed % 60 == 0 )); then
        log "  ...still waiting (${elapsed}s elapsed)..."
    fi
    if [[ $elapsed -ge $timeout_secs ]]; then
        fail "Timed out after ${timeout_secs}s waiting for $REPORT_PATH. Check $PLAYER_LOG for \"RWTH:\" lines."
    fi
done
log "Report file present."
if ! grep -q "RWTH: scenario complete" "$PLAYER_LOG" 2>/dev/null; then
    log "Warning: report file exists but the \"RWTH: scenario complete\" marker wasn't seen in Player.log — proceeding anyway."
fi

# A mod assembly that fails to type-load takes every probe and feature it registers with it, and
# says so only in Player.log. `Screenshot` steps carry on working, so the run yields a full set of
# plausible frames and can even come out green if nothing asserted a number. That is the worst shape
# a result can have, so it is a hard failure here rather than a line nobody reads.
#
# Safe to treat as ours: Step 3 writes a minimal modlist — Core, DLCs, Harmony, and the mods this run
# was given — so there is no third-party mod present to blame it on. The usual cause is a probe bridge
# and the mod it probes coming from two different builds (see Step 4c's warning).
#
# RWTH_ALLOW_TYPE_LOAD_ERRORS=1 downgrades it to a warning. Kept as an escape hatch rather than
# trusted-by-default because this gate was added from one observed failure: if some vanilla or DLC
# path turns out to log one of these harmlessly, a hard gate would block every run on the box, and
# nobody should have to edit the runner to get unblocked. Reach for it only after reading the lines it
# printed — the whole point is that these are invisible otherwise.
TYPE_LOAD_PATTERN="ReflectionTypeLoadException\|Could not resolve type with token\|Unable to load one or more of the requested types"
assert_no_type_load_failure() {
    local hits
    hits="$(grep -c "$TYPE_LOAD_PATTERN" "$PLAYER_LOG" 2>/dev/null || true)"
    if [[ "${hits:-0}" -eq 0 ]]; then
        return 0
    fi
    log "Player.log type-load errors:"
    grep -m 5 -A 2 "$TYPE_LOAD_PATTERN" "$PLAYER_LOG" 2>/dev/null | sed 's/^/[run_test]   /' || true
    local detail="a mod assembly failed to type-load ($hits line(s) in $PLAYER_LOG). Every probe and feature it registers is silently absent, so this run's results mean nothing — even the ones that passed. Usual cause: the probe bridge and the mod it probes came from different builds; check Step 4c's fingerprints above."
    if [[ "${RWTH_ALLOW_TYPE_LOAD_ERRORS:-0}" == "1" ]]; then
        log "Warning: $detail"
        log "         Continuing only because RWTH_ALLOW_TYPE_LOAD_ERRORS=1."
    else
        fail "$detail Set RWTH_ALLOW_TYPE_LOAD_ERRORS=1 to proceed anyway."
    fi
}
assert_no_type_load_failure

# Give RimWorldLinux a moment to quit on its own (ScenarioDriver calls Application.Quit()
# right after writing the report); kill it if it's still around after that.
sleep 3
if rimworld_alive; then
    log "RimWorld still running after report — stopping it."
    kill_rimworld
fi

# ---------------------------------------------------------------------------
# Step 7: parse and print the report
# ---------------------------------------------------------------------------
# ScenarioReport/ProbeCheckResult/SuiteReport are serialized with System.Text.Json's default
# (PascalCase) naming — Shared/SuiteReport.cs's SuiteReportSerializer calls JsonSerializer.Serialize
# with no naming policy — so the keys below match the C# property names exactly, not camelCase.
#
# Two shapes: a single-scenario run writes a bare ScenarioReport, a suite run writes a SuiteReport
# wrapping one per scenario. Told apart by the "Scenarios" key rather than by what this script asked
# for, so a report is readable on its own (e.g. re-inspected later) without knowing how it was made.
log "--- Step 7: results ---"
set +e
python3 - "$REPORT_PATH" <<'PYEOF'
import json, sys

report = json.load(open(sys.argv[1]))


def print_scenario(scenario, indent="  "):
    print(f"[run_test]{indent}scenario: {scenario.get('ScenarioName')}  pass={scenario.get('Pass')}")
    # A skip keeps Pass=true so a box without the DLC a scenario needs stays green, which makes this
    # line the only thing standing between "skipped" and a reader seeing pass=True over a scenario
    # that verified nothing. See Shared/ScenarioReport.cs.
    if scenario.get("Skipped"):
        print(f"[run_test]{indent}  SKIPPED: {scenario.get('SkipReason')}")
    for check in scenario.get("ProbeChecks", []):
        status = "PASS" if check.get("Pass") else "FAIL"
        print(f"[run_test]{indent}  {check.get('ProbeName')}: {status} "
              f"(actual={check.get('ActualValue')}, expected={check.get('ExpectedValue')}, "
              f"tolerance={check.get('Tolerance')})")
    # A timelapse contributes one screenshot path per frame, so listing them all would bury the probe
    # results under dozens of near-identical lines. Long runs get summarised instead.
    shots = scenario.get("ScreenshotPaths", [])
    if len(shots) > 8:
        print(f"[run_test]{indent}  screenshots: {len(shots)} files, {shots[0]} .. {shots[-1]}")
    else:
        for path in shots:
            print(f"[run_test]{indent}  screenshot: {path}")
    print_vision(scenario, indent)
    for err in scenario.get("Errors", []):
        print(f"[run_test]{indent}  ERROR: {err}")


# A vision assert nobody has judged does not fail the run (see Shared/VisionGate.cs), so it has to be
# impossible to miss here instead. A run that prints a bare "pass=True" while carrying unanswered
# rubrics would be a green result meaning less than it looks like.
def print_vision(scenario, indent):
    asserts = scenario.get("VisionAsserts", [])
    if not asserts:
        return

    pending = [a for a in asserts if a.get("Verdict") is None]
    for a in asserts:
        verdict = a.get("Verdict")
        if verdict is None:
            state = "PENDING REVIEW"
            detail = a.get("Expect") or a.get("Prompt", "")[:60]
        else:
            confident = verdict.get("Confidence", 0) >= a.get("ConfidenceGate", 0.7)
            if not confident:
                state = "NEEDS A HUMAN"
            else:
                state = "PASS" if verdict.get("Pass") else "FAIL"
            detail = f"{verdict.get('Reason', '')} (confidence={verdict.get('Confidence')})"
        print(f"[run_test]{indent}  vision {a.get('Id')}: {state} — {detail}")

    if pending:
        print(f"[run_test]{indent}  NOTE: {len(pending)} vision assert(s) awaiting review — "
              f"this result is provisional. See Runner/README.md, 'Vision asserts'.")


if "Scenarios" not in report:
    print_scenario(report, indent=" ")
    sys.exit(0 if report.get("Pass") else 1)

scenarios = report.get("Scenarios", [])
print(f"[run_test] suite: {len(scenarios)} scenario(s)  pass={report.get('Pass')}")
# Printed before the per-scenario detail: an isolation shortfall changes how every result below it
# should be read.
for note in report.get("IsolationNotes", []):
    print(f"[run_test]   isolation: {note}")
for scenario in scenarios:
    print_scenario(scenario)
for err in report.get("Errors", []):
    print(f"[run_test]   SUITE ERROR: {err}")

failed = [s.get("ScenarioName") for s in scenarios if not s.get("Pass")]
if failed:
    print(f"[run_test] failed scenario(s): {', '.join(str(name) for name in failed)}")
# Repeated as a suite-level summary line, not only per scenario: a long suite's skips scroll away, and
# "12 scenarios, all green" reads very differently once you know 12 of them skipped.
skipped = [s.get("ScenarioName") for s in scenarios if s.get("Skipped")]
if skipped:
    print(f"[run_test] skipped {len(skipped)}/{len(scenarios)} scenario(s), which verified NOTHING: "
          f"{', '.join(str(name) for name in skipped)}")
# An empty Scenarios list must NOT pass — the mod's own gate (ReportComparer.AllPass(SuiteReport))
# already refuses it, and this mirrors that rather than trusting the flag alone.
sys.exit(0 if report.get("Pass") and scenarios else 1)
PYEOF
report_rc=$?
set -e

# ---------------------------------------------------------------------------
# Step 8: stitch Timelapse / TickLapse frame sequences into videos
# ---------------------------------------------------------------------------
# A Timelapse step is desugared (Shared/TimelapseExpander.cs) into one SetTime/Wait/Screenshot
# triple per frame, so by the time the run finishes the reports folder holds a numbered PNG
# sequence and nothing else. Turning that into a video is a pure post-processing step, which is why
# it lives out here rather than in the mod: no Unity encoder, no extra in-game dependency.
#
# TickLapse (Shared/TickLapseExpander.cs) is a different sweep — ticks per frame instead of hours —
# but it deliberately emits the SAME numbered-PNG sequence, so from here down the two are one case.
# Only the step name and its per-step defaults differ, which is why they are read together below
# rather than given a second stitching path to drift from this one.
#
# Deliberately runs BEFORE the pass/fail gate below — a scenario whose probe failed is exactly when
# you most want to watch what the lighting actually did.
stitch_one_timelapse() {
    local prefix="$1" fps="$2"
    local pattern="$REPORTS_DIR/${prefix}_%04d.png"
    local out="$REPORTS_DIR/${prefix}.mp4"
    local count ff_err

    count="$(find "$REPORTS_DIR" -maxdepth 1 -name "${prefix}_[0-9][0-9][0-9][0-9].png" | wc -l)"
    if [[ "$count" -eq 0 ]]; then
        log "Warning: scenario declares a '$prefix' timelapse but no frames were written."
        return 0
    fi

    log "Stitching $count '$prefix' frame(s) at ${fps}fps -> $out"
    # -y overwrites a previous run's video. yuv420p and the even-dimension scale filter keep the
    # result playable in browsers and standard players, which reject odd widths or non-4:2:0 output.
    if ff_err="$(ffmpeg -nostdin -loglevel error -y -framerate "$fps" -i "$pattern" \
            -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2" -c:v libx264 -pix_fmt yuv420p "$out" 2>&1)"; then
        log "Timelapse video: $out"
        if [[ $DELETE_FRAMES -eq 1 ]]; then
            find "$REPORTS_DIR" -maxdepth 1 -name "${prefix}_[0-9][0-9][0-9][0-9].png" -delete
            log "Deleted $count source frame(s) (--delete-frames)."
        fi
    else
        # Never fatal: the frames are the evidence, the video is a convenience. Losing the encode
        # shouldn't turn a passing verification run into a failure.
        log "Warning: ffmpeg failed for '$prefix' — frames kept in $REPORTS_DIR. ffmpeg said: $ff_err"
    fi
}

# Bash mirror of Shared/SuiteScreenshots.PrefixFor + Qualify: in suite mode the mod prefixes every
# screenshot name with its scenario, because two independently authored scenarios will happily both
# ask for the same fileName and the second would silently overwrite the first. The rule is kept
# trivial (sanitize to [A-Za-z0-9._-], join with "__") precisely so this mirror can't drift in
# interesting ways; SuiteScreenshotsTests.QualifySpellingIsPinned locks the C# side. Change one, change
# both — and note stitch_one_timelapse already warns loudly when a declared prefix matches no frames,
# which is what a drift would look like.
qualified_prefix() {
    local scenario_name="$1" prefix="$2" sanitized
    sanitized="$(printf '%s' "$scenario_name" | sed 's/[^A-Za-z0-9._-]/_/g')"
    [[ -z "$sanitized" ]] && sanitized="scenario"
    printf '%s__%s' "$sanitized" "$prefix"
}

stitch_scenario_timelapses() {
    local scenario="$1" declared scenario_name prefix fps
    # Default prefix/fps per step type must match TimelapseExpander's and TickLapseExpander's own
    # defaults; the expanders validate the values, this only consumes them (fps affects playback
    # rate, not what gets captured). The two differ on purpose: an hour sweep is a slide show of a
    # day, a tick sweep is continuous motion, so they do not want the same playback rate.
    declared="$(jq -r '.steps[]? | select(.type == "Timelapse" or .type == "TickLapse")
                       | if .type == "Timelapse"
                         then [(.args.fileNamePrefix // "timelapse"), (.args.fps // "12")]
                         else [(.args.fileNamePrefix // "ticklapse"), (.args.fps // "20")]
                         end | @tsv' "$scenario")"
    [[ -z "$declared" ]] && return 0

    scenario_name="$(jq -r '.name' "$scenario")"
    while IFS=$'\t' read -r prefix fps; do
        if [[ -n "$prefix" ]]; then
            if (( SUITE_MODE )); then
                stitch_one_timelapse "$(qualified_prefix "$scenario_name" "$prefix")" "$fps"
            else
                stitch_one_timelapse "$prefix" "$fps"
            fi
        fi
    done <<< "$declared"
}

stitch_timelapses() {
    local any=0 scenario
    for scenario in "${SCENARIOS[@]}"; do
        jq -e '.steps[]? | select(.type == "Timelapse" or .type == "TickLapse")' "$scenario" >/dev/null 2>&1 && any=1
    done
    (( any )) || return 0

    if ! command -v ffmpeg >/dev/null; then
        log "Warning: timelapse frames were captured but ffmpeg is not on PATH — skipping stitch."
        log "         Frames remain in $REPORTS_DIR."
        return 0
    fi

    log "--- Step 8: stitching timelapse(s) ---"
    for scenario in "${SCENARIOS[@]}"; do
        stitch_scenario_timelapses "$scenario"
    done
}

stitch_timelapses

if [[ $report_rc -ne 0 ]]; then
    fail "run did not pass — see results above."
fi

log "SUCCESS: $RUN_LABEL passed. Report: $REPORT_PATH"
