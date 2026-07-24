# RimWorldTestHarness

A dev-only RimWorld 1.6 mod + driver script for automating in-game verification of the other mods
in this repo: launch RimWorld, replay a scripted scenario against a save, and either (a) check
numeric probe readings against expected values (pass/fail gate) or (b) capture screenshots for
visual review — or both in the same run.

**Status: implemented, gated on one manual step.** Every piece (`Mod/`, `Runner/run_test.sh`,
both test projects) is built and offline-tested. What's left is entirely a manual, non-scriptable
prerequisite: a fixture save (`Fixtures/minimal_colony.rws`, see `Fixtures/README.md`) that hasn't
been created yet. See `TODO.md` for the exact remaining checklist and `DESIGN.md` for the
architecture, including how save loading piggybacks on RimWorld's own autostart-save mechanism
rather than a custom loader.

## Layout

- `Shared/` — pure spec/report/comparer logic (`netstandard2.0`, no game dependency). Unit-tested.
- `Mod/` — the in-game driver (`net481`, Harmony): `HarnessMod` bootstrap, `ScenarioDriver`
  (tick-driven state machine), `Patch_DriveScenario`/`Patch_ForceDevMode`/`Patch_ForcedLatitude`,
  `Probes/`.
- `Runner/` — `run_test.sh`, the external launch/wait/gate script, plus `fetch_mods.sh` for
  downloading a scenario's Workshop dependencies.
- `Scenarios/` — example `ScenarioSpec` JSON.
- `Fixtures/` — save files scenarios load from (gitignored, manually created — see
  `Fixtures/README.md`).
- `Tests/RimWorldTestHarness.Tests/` — NUnit tests for `Shared/`.
- `Tests/RimWorldTestHarness.ApiTests/` — Mono.Cecil checks that the vanilla RimWorld/Unity API
  surface `Mod/` depends on still exists in the installed game.

## Build & test

```bash
./build.sh   # builds Mod/ (net481) -> 1.6/Assemblies/
./test.sh    # runs both Tests/ projects
```

## Why this exists

See the parent `../CLAUDE.md` and this repo's own `DESIGN.md` — in short, manual in-game
spot-checking doesn't scale, and doesn't leave anything for a future Claude session to re-run to
catch regressions.
