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
#   ./run_test.sh <path/to/scenario.json> [--no-teardown]
#
# Notes:
#   - Kill signal for RimWorldLinux: pkill -9 -x RimWorldLinux is safe; do NOT use -f (it matches
#     the shell's own command line).
#   - This kills/relaunches RimWorldLinux and temporarily swaps ModsConfig.xml/Saves/autostart.rws
#     (both backed up and restored) — same blast radius as MissileGirl/TestMods/run_test.sh.

set -euo pipefail

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
RIMWORLD="/home/deck/.local/share/Steam/steamapps/common/RimWorld"
MODS_DIR="$RIMWORLD/Mods"
CONFIG_DIR="/home/deck/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios"
PLAYER_LOG="$CONFIG_DIR/Player.log"
MODSCONFIG="$CONFIG_DIR/Config/ModsConfig.xml"
MODSCONFIG_BAK="/tmp/ModsConfig_rwth.bak.xml"
SAVES_DIR="$CONFIG_DIR/Saves"
AUTOSTART_SAVE="$SAVES_DIR/autostart.rws"
AUTOSTART_BAK="/tmp/autostart_rwth.bak.rws"
RIMWORLD_STDERR="/tmp/rimworld_rwth_stderr.log"

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
for arg in "$@"; do
    case "$arg" in
        --no-teardown) NO_TEARDOWN=1 ;;
        --delete-frames) DELETE_FRAMES=1 ;;
        *) SCENARIO="$arg" ;;
    esac
done
if [[ -z "$SCENARIO" ]]; then
    echo "[run_test] usage: run_test.sh <path/to/scenario.json> [--no-teardown] [--delete-frames]" >&2
    exit 2
fi
[[ -f "$SCENARIO" ]] || fail "scenario file not found: $SCENARIO"
SCENARIO="$(cd "$(dirname "$SCENARIO")" && pwd)/$(basename "$SCENARIO")"

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------
command -v jq >/dev/null || fail "jq not found on PATH."
command -v python3 >/dev/null || fail "python3 not found on PATH."
[[ -x "$RIMWORLD/RimWorldLinux" ]] || fail "RimWorldLinux not found at $RIMWORLD."

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
REPORT_PATH="$REPORTS_DIR/$(basename "$SCENARIO" .json)-$(date +%Y%m%d-%H%M%S).json"

log "Scenario: $SCENARIO_NAME (save=$SAVE_FILE)"
log "Report will be written to: $REPORT_PATH"

# ---------------------------------------------------------------------------
# Cleanup / teardown
# ---------------------------------------------------------------------------
CREATED_HARNESS_LINK=0
CREATED_CELESTIAL_LINK=0
CREATED_TESTMOD_LINK=0
AUTOSTART_BAK_MADE=0

cleanup_done=0
cleanup() {
    if [[ $cleanup_done -eq 1 ]]; then return; fi
    cleanup_done=1
    log "Cleaning up..."
    pkill -9 -x RimWorldLinux 2>/dev/null || true
    if [[ $NO_TEARDOWN -eq 0 ]]; then
        teardown
    else
        log "--no-teardown: leaving symlinks, ModsConfig, and Saves/autostart.rws in place."
    fi
}
trap cleanup EXIT INT TERM

teardown() {
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
rimworld_alive() {
    pgrep -x RimWorldLinux >/dev/null 2>&1 || kill -0 "${RIMWORLD_PID:-0}" 2>/dev/null
}

launch_rimworld() {
    local max_retries=5
    local attempt=0

    while true; do
        attempt=$((attempt + 1))
        log "Launching RimWorld (attempt $attempt / $max_retries)..."

        if pgrep -x RimWorldLinux >/dev/null 2>&1; then
            log "Found a stray RimWorldLinux process — killing it before launching."
            pkill -9 -x RimWorldLinux 2>/dev/null || true
            sleep 2
        fi

        : > "$PLAYER_LOG"
        : > "$RIMWORLD_STDERR"
        rm -f "$REPORT_PATH"

        # -quicktest (quicktest mode only): jumps straight from the entry scene into the Play
        # scene, where Root_Play.Start() generates a fresh colony (see Step 4). Harmless to omit
        # in fixture mode, where autostart.rws drives the load instead.
        local quicktest_arg=()
        [[ $USE_QUICKTEST -eq 1 ]] && quicktest_arg=(-quicktest)

        RWTH_SCENARIO="$SCENARIO" RWTH_REPORT="$REPORT_PATH" \
            "$RIMWORLD/RimWorldLinux" --no-sandbox \
            "${quicktest_arg[@]}" \
            -logfile "$PLAYER_LOG" \
            2>"$RIMWORLD_STDERR" &
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
    log "RimWorld still running after report — killing it."
    pkill -9 -x RimWorldLinux 2>/dev/null || true
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
