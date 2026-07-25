# Runner

`run_test.sh <scenario.json>` launches RimWorld with a scenario spec, waits for it to finish, and
gates on the resulting `ScenarioReport`.

Which mods it activates is a **runner argument**, not a scenario field: pass `--mod <folder>` once
per mod under test. That stays out of the scenario JSON deliberately — which mods a run activates
isn't part of "what to script in-game", and baking it into the spec would make the same scenario
unrunnable against a different modset.

```bash
# No --mod at all: this repo's own scenarios use only vanilla defs.
./run_test.sh ../Scenarios/daycycle_timelapse.json

# A mod under test plus its probe-bridge folder, in load order. Scenario paths are just args, so
# mod-specific scenarios live in that mod's own repo:
./run_test.sh --mod ../../SomeMod --mod ../../SomeMod/TestMod \
              ../../SomeMod/Tests/Scenarios/some_probe.json
```

Each `--mod` folder must contain `About/About.xml`; its `<packageId>` is read from there rather than
hardcoded anywhere in this script. The harness's own packageId is always appended **last**, so its
patches wrap the mod's and its probes read state those mods have already applied. A `--mod` with no
built assembly is warned about, not failed on — a Defs-only XML mod is a valid test subject.

Requires the scenario's `Fixtures/<saveFile>` to already exist (see `Fixtures/README.md` — manual,
not scriptable) and `RimWorldTestHarness` built (`../build.sh`), plus each `--mod` built if it ships
C#. Launches RimWorldLinux and temporarily swaps `ModsConfig.xml`/`Saves/autostart.rws` — both
backed up and restored on exit (`--no-teardown` to skip cleanup for post-mortem debugging).

`reports/` (gitignored) holds timestamped `ScenarioReport` JSON per run, plus any screenshots the
scenario captured.

## Suites: several scenarios in one boot

A boot costs minutes and a step costs milliseconds, so give it more than one scenario and they all
run inside a single game load:

```bash
# Several scenarios, one boot:
./run_test.sh ../Scenarios/daycycle_timelapse.json ../Scenarios/shadow_casters_daycycle.json
# The shell globs:
./run_test.sh --mod ../../SomeMod ../../SomeMod/Tests/Scenarios/*.json
# Or a checked-in list file:
./run_test.sh --suite ../Scenarios/demo_suite.txt
```

One scenario given positionally behaves exactly as it always has, report shape included. Two or more
(or any `--suite`) switches to suite mode: the report becomes a `SuiteReport` wrapping one
`ScenarioReport` per scenario, and screenshot names are prefixed with their scenario
(`<scenario>__<fileName>`) so independently authored scenarios can't overwrite each other's images.

Between scenarios the driver restores what it can (clock, latitude, feature flags, time speed, camera,
screenshot mode) and **reloads the save mid-session** where it cannot — after any scenario that ran
`PlaceThings`/`SetTerrain`, whose map mutations are not undoable. `--isolation=auto|always|never`
overrides that: `always` reloads before every scenario, `never` only soft-resets. See `DESIGN.md`,
"Batching scenarios into one load".

Two constraints worth knowing:

- **All scenarios in one run must declare the same `saveFile`** — it is installed once at boot as
  `autostart.rws` and reloaded from mid-run. Mixed fixtures are rejected up front rather than run
  against whichever came first. `requiredMods` are unioned.
- **Order matters for cost.** Put map-mutating scenarios last (or together at the end) and the suite
  pays for fewer reloads. The runner never reorders — a suite that ran in a different order than it was
  written would make a scenario's result depend on which others were in the run.

With no fixture the runner falls back to `-quicktest`, which generates its colony at boot and writes no
save — so there is nothing to reload, and a suite whose map-mutating scenario is followed by another
fails with an explicit error rather than quietly running the second against the first's world.

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

## Vision asserts: giving an LLM a rubric

A `Probe` checks that a formula returns the right number. It cannot check that the number reached the
screen — CelestialLighting #15 shipped with unit tests *and* a probe both green while the effect
rendered nothing. An `Assert` step with `kind: vision` closes that gap: it declares a rubric plus the
evidence to answer it, and the run emits that as a review packet.

```json
{ "type": "Assert", "args": {
    "kind": "vision",
    "images": "weather_clear.png,weather_rain.png",
    "prompt": "Two frames, same camera. The SECOND was captured under Rain. Confirm it is darker and desaturated, shows visible rain, and that neither frame is a black screen or broken render.",
    "expect": "second frame is darker, desaturated, and visibly raining",
    "confidenceGate": "0.7"
} }
```

`images` names `fileName`s captured by earlier `Screenshot` steps in the same scenario — a name that
was never captured fails the step rather than sending the judge a packet it cannot answer. For a
timelapse, name whichever frames matter (`daycycle_0000.png`, …); the judge reads still frames, not
video. Each assert also carries an excerpt of the game's **warnings and errors** from `Verse.Log`,
because a missing def or an exception thrown mid-step is invisible in a screenshot and obvious in the
log.

The excerpt is scoped to the current scenario, not the session — otherwise it fills with unrelated
startup warnings from whatever else is in the mod list, and a judge skims past evidence that was
supposed to be the point. **An empty `LogExcerpt` therefore means "this scenario logged nothing",
which is a clean result, not a missing capture.** Set `logLines: 0` to disable it outright.

**The harness does not call an LLM.** It emits the packet into the report; a Claude session or the
RimWorldDevMCP channel judges it and writes a verdict back. That keeps the gate free of an API key, a
per-run cost, and a network dependency.

### What blocks a run

| Verdict | Outcome |
|---|---|
| none yet | **pending** — does not block; the run is *provisionally* green |
| fail, confidence ≥ gate | **blocked** — fails the run |
| fail, confidence < gate | needs a human — does not block |
| pass, confidence < gate | needs a human — an unsure pass is not an approval |
| pass, confidence ≥ gate | passed |

Only a *confident* fail red-builds. An LLM judging a screenshot is a fallible reviewer, and a gate
that fails on its uncertain opinion gets switched off within a week — at which point it protects
nothing. A confident "this is broken" is exactly the signal a probe-green run was lying.

The leniency is only safe because the shortfall is stated out loud: `run_test.sh` prints every
assert's state and a `NOTE: N vision assert(s) awaiting review — this result is provisional`. A
silent provisional pass would be the green-run-means-less failure the rest of this repo is built to
avoid.

### Writing a verdict back

Verdicts live in the report JSON, under each assert's `Verdict`:

```json
"Verdict": { "Pass": false, "Confidence": 0.9, "Reason": "second frame is identical to the first — no rain visible" }
```

`Reason` is not optional in practice: the point of recording a verdict is that the next person
doesn't have to re-derive it.

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
