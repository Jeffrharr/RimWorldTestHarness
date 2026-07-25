#!/usr/bin/env bash
# Runner/run_test.sh — Live RimWorld scenario runner.
#
# Runs ONE scenario or a SUITE of them. A RimWorld boot costs minutes and a step costs
# milliseconds, so a suite runs every scenario inside a single game load, reloading the fixture
# mid-session between the ones that mutated the map (see DESIGN.md, "Batching scenarios into one
# load"). One scenario on the command line behaves exactly as it always has, report shape included.
#
# What this does:
#   1. Symlinks RimWorldTestHarness and every --mod folder into RimWorld's Mods folder if not
#      already present (a mod under test is often already symlinked as a real active mod — this
#      script never removes a symlink it didn't create).
#   2. Backs up the real ModsConfig.xml and any existing Saves/autostart.rws.
#   3. Writes a minimal ModsConfig.xml: Core + installed DLCs + brrainz.harmony + the union of
#      every selected scenario's requiredMods + each --mod's packageId (read from its own
#      About/About.xml) + joof.rimworldtestharness, which goes last so its patches wrap theirs.
#   4. Copies the scenarios' Fixtures/<saveFile> to Saves/autostart.rws — RimWorld's own vanilla
#      autostart mechanism (Root_Entry.Start -> SaveGameFilesUtility.GetAutostartSaveFile, gated
#      on Prefs.DevMode which Patch_ForceDevMode forces true while a scenario is active) loads it
#      with no custom load-driving code needed. See DESIGN.md.
#   5. Launches RimWorldLinux with RWTH_SCENARIO (single) or RWTH_SUITE (suite) plus RWTH_REPORT,
#      GPU-rendering (no -batchmode/-nographics — Screenshot steps need a real rendered frame),
#      reusing MissileGirl/TestMods/run_test.sh's --no-sandbox + retry-on-early-crash shape.
#   6. Waits for the report file to appear, gated on RimWorld staying alive and a timeout.
#   7. Parses the JSON report, prints each probe's pass/fail and any screenshot paths, exits 0
#      only if Pass == true (for a suite: only if every scenario passed and there were no
#      suite-level errors).
#   8. Stitches any Timelapse frame sequences into videos.
#   9. Restores ModsConfig.xml + Saves/autostart.rws from backup and removes any symlinks this
#      run created (unless --no-teardown).
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
#   --no-teardown          leave symlinks/ModsConfig/autostart.rws in place afterwards
#   --delete-frames        delete timelapse PNGs once stitched
#   --isolation=POLICY     auto (default) | always | never — how hard a suite works to isolate one
#                          scenario from the next. See Shared/SuitePlan.cs.
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
#   - This still relaunches RimWorldLinux and temporarily swaps the real
#     ModsConfig.xml/Saves/autostart.rws (both backed up and restored) unless RWTH_ISOLATE_SAVEDATA=1
#     — same blast radius as MissileGirl/TestMods/run_test.sh.

set -euo pipefail

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
RIMWORLD="/home/deck/.local/share/Steam/steamapps/common/RimWorld"
MODS_DIR="$RIMWORLD/Mods"

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
MODSCONFIG_BAK="$RUN_TMP_DIR/ModsConfig.bak.xml"
SAVES_DIR="$CONFIG_DIR/Saves"
AUTOSTART_SAVE="$SAVES_DIR/autostart.rws"
AUTOSTART_BAK="$RUN_TMP_DIR/autostart.bak.rws"
RIMWORLD_STDERR="$RUN_TMP_DIR/rimworld_stderr.log"

# Deliberately NOT per-run: the point of the lock is that every run contends for the same file.
LOCK_FILE="${RWTH_LOCK_FILE:-${TMPDIR:-/tmp}/rwth-run-$(id -u).lock}"

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

# Mod folders to activate alongside the harness — the mods whose probes/features the scenarios
# exercise. Repeatable, and legitimately empty: the harness's own scenarios under Scenarios/ use
# only vanilla defs, so a bare run needs no mod under test at all. Passed as paths rather than
# packageIds because the run has to symlink the folder into Mods/ as well as name it in ModsConfig,
# and only the folder knows both.
MODS_UNDER_TEST=()

abspath() { echo "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"; }

usage() {
    echo "[run_test] usage: run_test.sh <scenario.json> [more.json ...] [--suite <list.txt>]" >&2
    echo "[run_test]        [--mod <mod-folder>]... [--no-teardown] [--delete-frames]" >&2
    echo "[run_test]        [--isolation=auto|always|never] [--print-config]" >&2
    exit 2
}

while (( $# )); do
    case "$1" in
        --no-teardown) NO_TEARDOWN=1 ;;
        --delete-frames) DELETE_FRAMES=1 ;;
        --print-config) PRINT_CONFIG=1 ;;
        --isolation=*) ISOLATION="${1#--isolation=}" ;;
        --isolation) shift; ISOLATION="${1:-}" ;;
        --suite) shift; SUITE_LIST_IN="${1:-}" ;;
        --suite=*) SUITE_LIST_IN="${1#--suite=}" ;;
        --mod) shift; MODS_UNDER_TEST+=("${1:-}") ;;
        --mod=*) MODS_UNDER_TEST+=("${1#--mod=}") ;;
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

(( ${#SCENARIOS[@]} )) || usage

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
    echo "MODSCONFIG_BAK=$MODSCONFIG_BAK"
    echo "SAVES_DIR=$SAVES_DIR"
    echo "AUTOSTART_SAVE=$AUTOSTART_SAVE"
    echo "AUTOSTART_BAK=$AUTOSTART_BAK"
    echo "PLAYER_LOG=$PLAYER_LOG"
    echo "RIMWORLD_STDERR=$RIMWORLD_STDERR"
    echo "LOCK_FILE=$LOCK_FILE"
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
if pgrep -x RimWorldLinux >/dev/null 2>&1; then
    fail "a RimWorldLinux process is already running — close it first. This run will not kill it (it may be your game, or another agent's run)."
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
if (( ${#MOD_UT_DIRS[@]} )); then
    MOD_UT_IDS_JSON="$(python3 - ${MOD_UT_DIRS[@]+"${MOD_UT_DIRS[@]}"} <<'PYEOF'
import json, re, sys

ids = []
for d in sys.argv[1:]:
    with open(f"{d}/About/About.xml", encoding="utf-8") as f:
        m = re.search(r"<packageId>(.*?)</packageId>", f.read(), re.S | re.I)
    if not m:
        sys.exit(f"no <packageId> in {d}/About/About.xml")
    ids.append(m.group(1).strip().lower())
print(json.dumps(ids))
PYEOF
)" || fail "could not read a packageId from a --mod About/About.xml"
    log "Mods under test: $(echo "$MOD_UT_IDS_JSON" | jq -r 'join(", ")')"
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
# Link names this run created under Mods/, so teardown removes exactly those and never a symlink
# that was already there (a mod under test is often already installed as a real, played mod).
CREATED_LINKS=()
AUTOSTART_BAK_MADE=0
# The EXIT trap is armed before Step 2, so a failure in between (a bad symlink, say) used to reach the
# restore path with no backups taken — and "no backup" means "no prior autostart.rws existed", so it
# would delete the user's real save. Only undo the swap if we actually made it.
BACKUPS_TAKEN=0

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
        discard_run_scratch
    else
        log "--no-teardown: leaving symlinks, ModsConfig, Saves/autostart.rws, and $RUN_TMP_DIR in place."
    fi
}
trap cleanup EXIT INT TERM

# The config backups have done their job once teardown has restored them; leaving copies of the user's
# ModsConfig/Prefs lying around in /tmp is pointless. Player.log and the stderr log stay — they're the
# post-mortem evidence every failure message points at — so the dir is only removed if it ends up
# empty, i.e. on a clean run with nothing worth keeping.
discard_run_scratch() {
    rm -f "$MODSCONFIG_BAK" "$AUTOSTART_BAK"
    [[ "$ISOLATE_SAVEDATA" == "1" ]] && rm -rf "$CONFIG_DIR"
    rmdir "$RUN_TMP_DIR" 2>/dev/null || log "Logs kept in $RUN_TMP_DIR"
}

restore_swapped_config() {
    log "Restoring ModsConfig from backup..."
    if [[ -f "$MODSCONFIG_BAK" ]]; then
        cp "$MODSCONFIG_BAK" "$MODSCONFIG"
        log "ModsConfig restored."
    else
        log "Warning: no ModsConfig backup found at $MODSCONFIG_BAK"
    fi

    log "Restoring Saves/autostart.rws..."
    if [[ $AUTOSTART_BAK_MADE -eq 1 ]]; then
        cp "$AUTOSTART_BAK" "$AUTOSTART_SAVE"
        log "autostart.rws restored from backup."
    else
        rm -f "$AUTOSTART_SAVE"
        log "No prior autostart.rws existed — removed the fixture copy."
    fi
}

teardown() {
    if [[ $BACKUPS_TAKEN -eq 1 ]]; then
        restore_swapped_config
    else
        log "Nothing to restore — the run never got as far as backing up ModsConfig/autostart.rws."
    fi

    log "Removing symlinks this run created..."
    local link_name
    for link_name in ${CREATED_LINKS[@]+"${CREATED_LINKS[@]}"}; do
        rm -f "$MODS_DIR/$link_name"
    done
}

# ---------------------------------------------------------------------------
# Step 1: symlinks (idempotent — never touch a symlink/dir that already existed,
# a mod under test is often already active as a real, played mod)
# ---------------------------------------------------------------------------
setup_symlink() {
    local target="$1" link_name="$2"
    if [[ -e "$MODS_DIR/$link_name" || -L "$MODS_DIR/$link_name" ]]; then
        log "Mods/$link_name already present — leaving as-is."
    else
        ln -s "$target" "$MODS_DIR/$link_name"
        CREATED_LINKS+=("$link_name")
        log "Symlinked Mods/$link_name -> $target"
    fi
}
log "--- Step 1: mod symlinks ---"
setup_symlink "$REPO_DIR" "RimWorldTestHarness"
for i in "${!MOD_UT_DIRS[@]}"; do
    setup_symlink "${MOD_UT_DIRS[$i]}" "${MOD_UT_LINKS[$i]}"
done

# ---------------------------------------------------------------------------
# Step 2: back up ModsConfig.xml and any existing autostart.rws
# ---------------------------------------------------------------------------
log "--- Step 2: backups ---"
[[ -f "$MODSCONFIG" ]] || fail "ModsConfig.xml not found at $MODSCONFIG — has RimWorld been run at least once?"
cp "$MODSCONFIG" "$MODSCONFIG_BAK"
log "Backed up ModsConfig.xml -> $MODSCONFIG_BAK"

mkdir -p "$SAVES_DIR"
if [[ -f "$AUTOSTART_SAVE" ]]; then
    cp "$AUTOSTART_SAVE" "$AUTOSTART_BAK"
    AUTOSTART_BAK_MADE=1
    log "Backed up existing autostart.rws -> $AUTOSTART_BAK"
else
    log "No existing autostart.rws — nothing to back up."
fi
# From here on teardown has something real to undo (see BACKUPS_TAKEN above).
BACKUPS_TAKEN=1

# ---------------------------------------------------------------------------
# Step 3: write minimal ModsConfig.xml
# ---------------------------------------------------------------------------
log "--- Step 3: writing minimal ModsConfig.xml ---"
RIMWORLD="$RIMWORLD" REQUIRED_MODS_JSON="$REQUIRED_MODS_JSON" \
    MOD_UT_IDS_JSON="$MOD_UT_IDS_JSON" python3 - "$MODSCONFIG" <<'PYEOF'
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
dlc_ids = [pid for (folder, pid) in dlc_candidates if os.path.isdir(os.path.join(data_dir, folder))]

required = json.loads(os.environ.get("REQUIRED_MODS_JSON", "[]"))
mods_under_test = json.loads(os.environ.get("MOD_UT_IDS_JSON", "[]"))

# The harness goes LAST, after every mod under test, for the reason About.xml's <loadAfter> states:
# its patches must wrap theirs and its probes must read state those mods have already applied. Mods
# under test keep the order they were passed in, so a mod plus its probe-bridge assembly land in the
# order their author asked for rather than one this script invented.
#
# De-duplicated because a scenario's requiredMods may legitimately name a mod that is also under
# test, and a packageId listed twice makes RimWorld refuse to boot.
active = []
for pid in (["ludeon.rimworld"] + dlc_ids + ["brrainz.harmony"] + required +
            mods_under_test + ["joof.rimworldtestharness"]):
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
    log "Removed any autostart.rws (backed up in Step 2, restored on teardown)."
fi

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
# An empty Scenarios list must NOT pass — the mod's own gate (ReportComparer.AllPass(SuiteReport))
# already refuses it, and this mirrors that rather than trusting the flag alone.
sys.exit(0 if report.get("Pass") and scenarios else 1)
PYEOF
report_rc=$?
set -e

# ---------------------------------------------------------------------------
# Step 8: stitch Timelapse frame sequences into videos
# ---------------------------------------------------------------------------
# A Timelapse step is desugared (Shared/TimelapseExpander.cs) into one SetTime/Wait/Screenshot
# triple per frame, so by the time the run finishes the reports folder holds a numbered PNG
# sequence and nothing else. Turning that into a video is a pure post-processing step, which is why
# it lives out here rather than in the mod: no Unity encoder, no extra in-game dependency.
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
    # Default fps here must match TimelapseExpander.DefaultFps; the expander validates the value,
    # this only consumes it (fps affects playback rate, not what gets captured).
    declared="$(jq -r '.steps[]? | select(.type == "Timelapse")
                       | [(.args.fileNamePrefix // "timelapse"), (.args.fps // "12")] | @tsv' "$scenario")"
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
        jq -e '.steps[]? | select(.type == "Timelapse")' "$scenario" >/dev/null 2>&1 && any=1
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
