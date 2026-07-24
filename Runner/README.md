# Runner

`run_test.sh <scenario.json>` launches RimWorld with a scenario spec, waits for it to finish, and
gates on the resulting `ScenarioReport`. Currently hardcoded to CelestialLighting as the mod under
test (symlinks, ModsConfig entries) — generalizing to other mods would mean parameterizing that
list instead of adding a scenario JSON field, since which mods it activates isn't really part of
"what to script in-game".

```bash
./run_test.sh ../Scenarios/shadow_lean_equinox.json
```

Requires the scenario's `Fixtures/<saveFile>` to already exist (see `Fixtures/README.md` — manual,
not scriptable) and `RimWorldTestHarness`/`CelestialLighting`/`CelestialLighting/TestMod` all built
(`build.sh` in each). Kills/relaunches RimWorldLinux and temporarily swaps
`ModsConfig.xml`/`Saves/autostart.rws` — both backed up and restored on exit (`--no-teardown` to
skip cleanup for post-mortem debugging).

`reports/` (gitignored) holds timestamped `ScenarioReport` JSON per run, plus any screenshots the
scenario captured.

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
