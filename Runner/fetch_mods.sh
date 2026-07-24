#!/usr/bin/env bash
# Runner/fetch_mods.sh — downloads a scenario's RequiredMods (Shared/ScenarioSpec.cs) from the
# Steam Workshop via SteamCMD, skipping anything already present.
#
# Uses an ANONYMOUS SteamCMD login (+login anonymous) — no personal Steam account, no credentials,
# no keychain, no Steam Guard. Logging in with a real account here has a nasty side effect: SteamCMD
# takes over the account's cached session on this machine, which "bumps" the logged-in Steam client
# and causes problems the next time RimWorld is launched through Steam. Anonymous login avoids that
# entirely.
#
# Caveat: RimWorld (appid 294100) is a paid app, and Steam sometimes refuses anonymous
# `workshop_download_item` for paid apps. If that happens, SteamCMD prints its own error and this
# script fails — there's no credential fallback here by design. In that case, download the mod(s)
# through the normal in-game Steam Workshop UI instead.
#
# Usage:
#   ./fetch_mods.sh ../Scenarios/some_scenario.json

set -euo pipefail

RIMWORLD_APPID=294100
WORKSHOP_DIR="/home/deck/.local/share/Steam/steamapps/workshop/content/$RIMWORLD_APPID"

log() { echo "[fetch_mods] $*"; }
fail() { echo "[fetch_mods] FAIL: $*" >&2; exit 1; }

SCENARIO="${1:-}"
[[ -n "$SCENARIO" ]] || fail "usage: fetch_mods.sh <path/to/scenario.json>"
[[ -f "$SCENARIO" ]] || fail "scenario file not found: $SCENARIO"
command -v steamcmd >/dev/null || fail "steamcmd not found on PATH."
command -v jq >/dev/null || fail "jq not found on PATH (needed to parse the scenario's requiredMods)."

# packageId->workshopId map as "id packageId" lines (packageId is only used for logging clarity).
mapfile -t REQUIRED_IDS < <(jq -r '.requiredMods // {} | to_entries[] | "\(.value) \(.key)"' "$SCENARIO")

if [[ ${#REQUIRED_IDS[@]} -eq 0 ]]; then
    log "No requiredMods in $SCENARIO — nothing to fetch."
    exit 0
fi

MISSING_IDS=()
for entry in "${REQUIRED_IDS[@]}"; do
    id="${entry%% *}"
    packageId="${entry#* }"
    if [[ -d "$WORKSHOP_DIR/$id" ]]; then
        log "OK: $packageId ($id) already present."
    else
        log "MISSING: $packageId ($id)"
        MISSING_IDS+=("$id")
    fi
done

if [[ ${#MISSING_IDS[@]} -eq 0 ]]; then
    log "All required mods already present."
    exit 0
fi

DOWNLOAD_ARGS=()
for id in "${MISSING_IDS[@]}"; do
    DOWNLOAD_ARGS+=(+workshop_download_item "$RIMWORLD_APPID" "$id")
done

log "Downloading ${#MISSING_IDS[@]} mod(s) via SteamCMD (anonymous login)..."
if ! steamcmd +login anonymous "${DOWNLOAD_ARGS[@]}" +quit; then
    fail "SteamCMD exited non-zero. Anonymous login can't download workshop items for some paid" \
         "apps — if that's the error above, fetch the mod(s) via the in-game Steam Workshop UI instead."
fi

FAILED=0
for id in "${MISSING_IDS[@]}"; do
    if [[ -d "$WORKSHOP_DIR/$id" ]]; then
        log "Downloaded: $id"
    else
        log "STILL MISSING after download attempt: $id"
        FAILED=1
    fi
done

[[ $FAILED -eq 0 ]] || fail "one or more mods failed to download — see SteamCMD output above."
log "All required mods present."
