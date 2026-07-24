# TODO

Status: all pieces are implemented and offline-tested (`Shared/`, `Mod/`, `Runner/run_test.sh`,
`Tests/RimWorldTestHarness.Tests`, `Tests/RimWorldTestHarness.ApiTests`). What's left is entirely
gated on a manual, non-scriptable step:

- [ ] **First fixture save** — `Fixtures/minimal_colony.rws`, created manually per
  `Fixtures/README.md`. Blocks the one remaining item below.
- [ ] **Live end-to-end run** — once the fixture exists, run
  `Runner/run_test.sh Scenarios/shadow_lean_equinox.json` and confirm it exits 0, prints
  `shadow_lean: PASS`, and leaves a screenshot under `Runner/reports/`.

## Done

- [x] **Save loading** — no custom loader needed. RimWorld's own `Root_Entry.Start()` autoloads a
  save named `autostart.rws` when `Prefs.DevMode` is true (`Patch_ForceDevMode.cs` forces that
  while a scenario is active); `Runner/run_test.sh` just drops the fixture into place under that
  name before launch. See `DESIGN.md`'s "Save loading: the vanilla autostart mechanism".
- [x] **Step implementations** — `Mod/ScenarioDriver.cs`, driven by `Patch_DriveScenario.cs`'s
  postfix on `Root_Play.Update()`.
- [x] **Forced latitude** — `Patch_ForcedLatitude.cs` postfixes `WorldGrid.LongLatOf` so `SetTile`
  steps don't need the fixture's actual landing tile to match a scenario's latitude.
- [x] **`shadow_lean` probe** — `CelestialLighting/Source/Probes/ShadowLeanProbe.cs` +
  `CelestialLighting/TestMod/` (separate mod project so the shipped `CelestialLighting.dll` never
  references this harness — see `DESIGN.md`'s "Where probe tests live").
- [x] **Report writing** — `ScenarioDriver.Finish()` serializes `ScenarioReport` to `RWTH_REPORT`
  and logs `"RWTH: scenario complete"` as a redundant Player.log marker.
- [x] **`Runner/run_test.sh`** — symlink setup, ModsConfig/autostart-save backup-and-restore,
  launch-with-retry (mirroring `MissileGirl/TestMods/run_test.sh`'s shape), report polling, gate
  checking.
- [x] **`Tests/RimWorldTestHarness.ApiTests`** — Mono.Cecil checks (pattern:
  `PerformanceSearch/Tests/SearchFix.Tests/`) for every vanilla member `Mod/` depends on, including
  the autostart chain we never call directly (`Root_Entry.Start`, `SaveGameFilesUtility.
  GetAutostartSaveFile`, `GameDataSaveLoader.LoadGame(FileInfo)`). Wired into `test.sh`.
- [x] `git init` this repo.
- [x] **`Runner/fetch_mods.sh`** — downloads a scenario's `RequiredMods` via SteamCMD. Wired into
  `run_test.sh`'s ModsConfig generation (reads `requiredMods` and includes them in `activeMods`).
