# Runner

`run_test.sh <scenario.json>` launches RimWorld with a scenario spec, waits for it to finish, and
gates on the resulting `ScenarioReport`. Currently hardcoded to CelestialLighting as the mod under
test (symlinks, ModsConfig entries) — generalizing to other mods would mean parameterizing that
list instead of adding a scenario JSON field, since which mods it activates isn't really part of
"what to script in-game".

```bash
# A scenario path is just an arg, so mod-specific scenarios live in the mod's own repo:
./run_test.sh ../../CelestialLighting/Tests/Scenarios/shadow_lean_equinox.json
# The harness's own modset-agnostic demos still live here:
./run_test.sh ../Scenarios/daycycle_timelapse.json
```

Requires the scenario's `Fixtures/<saveFile>` to already exist (see `Fixtures/README.md` — manual,
not scriptable) and `RimWorldTestHarness`/`CelestialLighting`/`CelestialLighting/TestMod` all built
(`build.sh` in each). Launches RimWorldLinux and temporarily swaps
`ModsConfig.xml`/`Saves/autostart.rws` — both backed up and restored on exit (`--no-teardown` to
skip cleanup for post-mortem debugging).

`reports/` (gitignored) holds timestamped `ScenarioReport` JSON per run, plus any screenshots the
scenario captured.

## One run at a time (issue #6)

The run mutates global machine state, so it defends itself rather than trusting that it is alone:

- **It refuses to start** if any `RimWorldLinux` is already running, or if another `run_test.sh` holds
  the run lock (`/tmp/rwth-run-<uid>.lock`, an exclusive `flock` held for the life of the script). Both
  are hard failures with a message, not waits. Close the game, or wait for the other run.
- **It only ever kills the game it started**, by PID — SIGTERM, escalating to SIGKILL after 10s. It no
  longer `pkill`s by name, so your own session survives.
- **Per-run scratch dir** `/tmp/rwth-run-<timestamp>-<pid>/` holds the `ModsConfig`/`autostart`
  backups, this run's `Player.log`, and the game's stderr. Note `Player.log` moved here from the game
  config dir: the script greps it for progress markers, so it cannot be shared. The dir is removed on
  a clean teardown unless logs are worth keeping; `--no-teardown` always keeps it.

`--print-config` prints every path the run would use and exits without launching, locking, or creating
anything — handy to confirm two invocations really are isolated.

Overridable: `RWTH_CONFIG_DIR` (game save-data root), `RWTH_RUN_TMP_DIR`, `RWTH_LOCK_FILE`.

### `RWTH_ISOLATE_SAVEDATA=1` — opt-in, not yet validated by a live run

Gives the run its own save-data root (`<run scratch>/savedata`, seeded with a copy of the real
`Config/`) instead of mutating the user's, via RimWorld's own `-savedatafolder=` command-line arg
(`Verse.GenFilePaths.SaveDataFolderPath`). The run then **asserts** that `Player.log` contains
`Save data folder overridden to <our dir>` and fails loudly if it does not, so it cannot quietly fall
back to the real config dir.

Off by default because a fresh root is a real behaviour change — RimWorld sees no `Screenshots/`, an
empty `Saves/`, and only the `Config/` we seed — and no live run has confirmed the game boots happily
that way. Turn it on deliberately, expect to debug it, and note that scenario screenshots are
unaffected (they are written next to the report in `reports/`).

## Two verification modes, one scenario format

A `ScenarioSpec` can mix `Probe` steps (numeric pass/fail — the spec-driven gate,
`Shared/ReportComparer.cs`) and `Screenshot` steps (visual-confirm — a human or Claude reviews the
image afterward) in the same run. Neither mode affects the other: `ScenarioReport.Pass` is decided
purely by probe checks, and screenshot paths are just listed alongside for review.

## Prerequisites once implemented

- RimWorld installed at the standard Steam path.
- The scenario's `saveFile` already exists under `Fixtures/` — see `Fixtures/README.md` (save
  creation is manual, not scriptable).
- `Mod/RimWorldTestHarness.csproj` built (`../build.sh`) so `1.6/Assemblies/` has a current DLL.
- Any `requiredMods` the scenario declares are present in the Steam Workshop content folder — run
  `fetch_mods.sh` first (below) if unsure.

## `fetch_mods.sh` — downloading a scenario's Workshop dependencies

```bash
./fetch_mods.sh ../Scenarios/some_scenario.json
```

Reads the scenario's `requiredMods` (`Shared/ScenarioSpec.cs`, a packageId -> Steam Workshop file
id map), skips anything already under
`~/.local/share/Steam/steamapps/workshop/content/294100/`, and downloads the rest via `steamcmd`.

**Uses an anonymous SteamCMD login** (`+login anonymous`) — no personal account, no credentials, no
Steam Guard. Logging in with a real account here takes over the account's cached Steam session on
this machine, which bumps the logged-in Steam client and breaks the next RimWorld launch through
Steam; anonymous login avoids that.

**Caveat:** RimWorld (appid 294100) is a paid app, and Steam sometimes refuses anonymous
`workshop_download_item` for paid apps. If SteamCMD errors out that way, the script fails (there's
no credential fallback by design) — download the mod(s) through the normal in-game Steam Workshop
UI instead.
