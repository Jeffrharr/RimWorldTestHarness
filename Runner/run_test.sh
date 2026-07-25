#!/usr/bin/env bash
# Runner/run_test.sh — Live RimWorld scenario runner.
#
# What this does:
#   1. Symlinks RimWorldTestHarness, CelestialLighting, and CelestialLighting/TestMod into
#      RimWorld's Mods folder if not already present (CelestialLighting is normally already
#      symlinked as a real active mod — this script never removes a symlink it didn't create).
#   2. Backs up the real ModsConfig.xml and any existing Saves/autostart.rws.
#   3. Writes a minimal ModsConfig.xml: Core + installed DLCs + brrainz.harmony + the scenario's
#      requiredMods + joof.celestiallighting + joof.rimworldtestharness +
#      joof.celestiallighting.probes.
#   4. Copies the scenario's Fixtures/<saveFile> to Saves/autostart.rws — RimWorld's own vanilla
#      autostart mechanism (Root_Entry.Start -> SaveGameFilesUtility.GetAutostartSaveFile, gated
#      on Prefs.DevMode which Patch_ForceDevMode forces true while a scenario is active) loads it
#      with no custom load-driving code needed. See DESIGN.md.
#   5. Launches RimWorldLinux with RWTH_SCENARIO/RWTH_REPORT set, GPU-rendering (no
#      -batchmode/-nographics — Screenshot steps need a real rendered frame), reusing
#      MissileGirl/TestMods/run_test.sh's --no-sandbox + retry-on-early-crash shape.
#   6. Waits for the report file to appear, gated on RimWorld staying alive and a timeout.
#   7. Parses the JSON report, prints each probe's pass/fail and any screenshot paths, exits 0
#      only if Pass == true.
#   8. Restores ModsConfig.xml + Saves/autostart.rws from backup and removes any symlinks this
#      run created (unless --no-teardown).
#
# Usage:
#   ./run_test.sh <path/to/scenario.json> [--no-teardown] [--delete-frames] [--print-config]
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
# The mod under test normally sits in a sibling repo, but that only holds for the canonical checkout:
# a git worktree (the mandated workflow when several agents share this repo set) lives under
# .worktrees/, where no sibling CelestialLighting exists. Overridable so a worktree can still do a live
# run against the real mod instead of dying on a bare `cd`.
CELESTIAL_DIR_RAW="${RWTH_CELESTIAL_DIR:-$REPO_DIR/../CelestialLighting}"
if [[ ! -d "$CELESTIAL_DIR_RAW" ]]; then
    echo "[run_test] FAIL: mod-under-test dir not found at $CELESTIAL_DIR_RAW." >&2
    echo "[run_test]   Set RWTH_CELESTIAL_DIR to CelestialLighting's checkout — needed when running" >&2
    echo "[run_test]   from a git worktree, which has no sibling copy." >&2
    exit 1
fi
CELESTIAL_DIR="$(cd "$CELESTIAL_DIR_RAW" && pwd)"
TESTMOD_DIR="$CELESTIAL_DIR/TestMod"
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
SCENARIO=""
NO_TEARDOWN=0
DELETE_FRAMES=0
PRINT_CONFIG=0
for arg in "$@"; do
    case "$arg" in
        --no-teardown) NO_TEARDOWN=1 ;;
        --delete-frames) DELETE_FRAMES=1 ;;
        --print-config) PRINT_CONFIG=1 ;;
        *) SCENARIO="$arg" ;;
    esac
done
if [[ -z "$SCENARIO" ]]; then
    echo "[run_test] usage: run_test.sh <path/to/scenario.json> [--no-teardown] [--delete-frames] [--print-config]" >&2
    exit 2
fi
[[ -f "$SCENARIO" ]] || fail "scenario file not found: $SCENARIO"
SCENARIO="$(cd "$(dirname "$SCENARIO")" && pwd)/$(basename "$SCENARIO")"

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
    echo "SCENARIO=$SCENARIO"
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

SCENARIO_NAME="$(jq -r '.name' "$SCENARIO")"
SAVE_FILE="$(jq -r '.saveFile' "$SCENARIO")"
REQUIRED_MODS_JSON="$(jq -c '[.requiredMods // {} | keys[]]' "$SCENARIO")"

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
CELESTIAL_DLL="$CELESTIAL_DIR/1.6/Assemblies/CelestialLighting.dll"
PROBES_DLL="$TESTMOD_DIR/1.6/Assemblies/CelestialLighting.Probes.dll"
[[ -f "$HARNESS_DLL" ]]   || fail "$HARNESS_DLL missing — run RimWorldTestHarness/build.sh first."
[[ -f "$CELESTIAL_DLL" ]] || fail "$CELESTIAL_DLL missing — run CelestialLighting/build.sh first."
[[ -f "$PROBES_DLL" ]]    || fail "$PROBES_DLL missing — run CelestialLighting/TestMod/build.sh first."

mkdir -p "$REPORTS_DIR"
# 700: this dir holds a copy of the user's ModsConfig/Prefs and lives in a world-readable /tmp.
mkdir -p -m 700 "$RUN_TMP_DIR"
REPORT_PATH="$REPORTS_DIR/$(basename "$SCENARIO" .json)-$(date +%Y%m%d-%H%M%S).json"

log "Scenario: $SCENARIO_NAME (save=$SAVE_FILE)"
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
CREATED_HARNESS_LINK=0
CREATED_CELESTIAL_LINK=0
CREATED_TESTMOD_LINK=0
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
    [[ $CREATED_HARNESS_LINK -eq 1 ]]   && rm -f "$MODS_DIR/RimWorldTestHarness"
    [[ $CREATED_CELESTIAL_LINK -eq 1 ]] && rm -f "$MODS_DIR/CelestialLighting"
    [[ $CREATED_TESTMOD_LINK -eq 1 ]]   && rm -f "$MODS_DIR/CelestialLighting.Probes"
}

# ---------------------------------------------------------------------------
# Step 1: symlinks (idempotent — never touch a symlink/dir that already existed,
# e.g. CelestialLighting is normally already active as a real mod)
# ---------------------------------------------------------------------------
setup_symlink() {
    local target="$1" link_name="$2" var_name="$3"
    if [[ -e "$MODS_DIR/$link_name" || -L "$MODS_DIR/$link_name" ]]; then
        log "Mods/$link_name already present — leaving as-is."
    else
        ln -s "$target" "$MODS_DIR/$link_name"
        printf -v "$var_name" '%d' 1
        log "Symlinked Mods/$link_name -> $target"
    fi
}
log "--- Step 1: mod symlinks ---"
setup_symlink "$REPO_DIR"      "RimWorldTestHarness"        CREATED_HARNESS_LINK
setup_symlink "$CELESTIAL_DIR" "CelestialLighting"           CREATED_CELESTIAL_LINK
setup_symlink "$TESTMOD_DIR"   "CelestialLighting.Probes"    CREATED_TESTMOD_LINK

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
RIMWORLD="$RIMWORLD" REQUIRED_MODS_JSON="$REQUIRED_MODS_JSON" python3 - "$MODSCONFIG" <<'PYEOF'
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

active = (
    ["ludeon.rimworld"] + dlc_ids + ["brrainz.harmony"] + required +
    ["joof.celestiallighting", "joof.rimworldtestharness", "joof.celestiallighting.probes"]
)

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

        RWTH_SCENARIO="$SCENARIO" RWTH_REPORT="$REPORT_PATH" \
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
# ScenarioReport/ProbeCheckResult are serialized with System.Text.Json's default (PascalCase)
# naming — Mod/ScenarioDriver.cs's Finish() calls JsonSerializer.Serialize with no naming policy —
# so the keys below match the C# property names exactly, not camelCase.
log "--- Step 7: results ---"
set +e
python3 - "$REPORT_PATH" <<'PYEOF'
import json, sys

report = json.load(open(sys.argv[1]))
print(f"[run_test] scenario: {report.get('ScenarioName')}  pass={report.get('Pass')}")
for check in report.get("ProbeChecks", []):
    status = "PASS" if check.get("Pass") else "FAIL"
    print(f"[run_test]   {check.get('ProbeName')}: {status} "
          f"(actual={check.get('ActualValue')}, expected={check.get('ExpectedValue')}, "
          f"tolerance={check.get('Tolerance')})")
# A timelapse contributes one screenshot path per frame, so listing them all would bury the probe
# results under dozens of near-identical lines. Long runs get summarised instead.
shots = report.get("ScreenshotPaths", [])
if len(shots) > 8:
    print(f"[run_test]   screenshots: {len(shots)} files, {shots[0]} .. {shots[-1]}")
else:
    for path in shots:
        print(f"[run_test]   screenshot: {path}")
for err in report.get("Errors", []):
    print(f"[run_test]   ERROR: {err}")
sys.exit(0 if report.get("Pass") else 1)
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

stitch_timelapses() {
    # Default fps here must match TimelapseExpander.DefaultFps; the expander validates the value,
    # this only consumes it (fps affects playback rate, not what gets captured).
    local declared
    declared="$(jq -r '.steps[]? | select(.type == "Timelapse")
                       | [(.args.fileNamePrefix // "timelapse"), (.args.fps // "12")] | @tsv' "$SCENARIO")"
    [[ -z "$declared" ]] && return 0

    if ! command -v ffmpeg >/dev/null; then
        log "Warning: timelapse frames were captured but ffmpeg is not on PATH — skipping stitch."
        log "         Frames remain in $REPORTS_DIR."
        return 0
    fi

    log "--- Step 8: stitching timelapse(s) ---"
    while IFS=$'\t' read -r prefix fps; do
        [[ -n "$prefix" ]] && stitch_one_timelapse "$prefix" "$fps"
    done <<< "$declared"
}

stitch_timelapses

if [[ $report_rc -ne 0 ]]; then
    fail "scenario did not pass — see probe results above."
fi

log "SUCCESS: $SCENARIO_NAME passed. Report: $REPORT_PATH"
