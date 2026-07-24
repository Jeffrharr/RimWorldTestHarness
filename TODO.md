# TODO

Status: all pieces are implemented and offline-tested (`Shared/`, `Mod/`, `Runner/run_test.sh`,
`Tests/RimWorldTestHarness.Tests`, `Tests/RimWorldTestHarness.ApiTests`). Remaining work:

- [x] **First fixture save** — `Fixtures/minimal_colony.rws` is now in place, copied from the
  user's newest live colony save ("Tehenussia Unification"). Note it's a real, mod-heavy
  permadeath colony, *not* a minimal one — good enough to unblock live runs, but see the fixture
  ideas below for why we'll want something leaner and reproducible.
- [x] **Live end-to-end run** — `Runner/run_test.sh Scenarios/shadow_lean_equinox.json` passes
  against the fixture: exits 0, `shadow_lean: PASS` (actual `1.02e-06`, tol `0.02`), screenshot in
  `Runner/reports/`. Confirmed the mod-heavy fixture loads cleanly under the minimal ModsConfig.

## Fixture ideas (future)

The current fixture is whatever save happened to be newest — heavy, tied to the user's exact
active mod list, and not something a fresh checkout can regenerate. Directions to make the
fixture story self-serve instead of manual:

- [ ] **Quickstart / no-fixture mode** (lightest — for when the colony doesn't matter) — for
  lighting scenarios the specific colony is irrelevant: `Patch_ForcedLatitude` already overrides
  latitude and the `SetSeason`/`SetTime` steps drive the clock, so only the sky matters. Add a
  scenario flag (e.g. `"fixture": "quickstart"` or a top-level `"quickstart": true`) that skips
  the autostart-save drop entirely and instead launches RimWorld with the vanilla `quicktest`
  command-line arg. `Verse.QuickStarter.CheckQuickStart()` sees that arg and jumps straight to the
  Play scene, where `Root_Play.SetupForQuickTestPlay()` generates a throwaway Crashlanded colony
  (0.3 planet coverage, random seed, Cassandra/Rough) with no save file. Removes the manual
  fixture prerequisite for the common case; the seed is random but that's fine since latitude is
  forced (pin the seed later only if a scenario needs determinism).
- [ ] **Generate a save from JSON params** (preferred when the colony *does* matter) — extend the
  harness so a scenario (or a
  companion `FixtureSpec`) declares the world/colony parameters (seed, biome, latitude, tile,
  starting pawns, scenario preset) and the driver programmatically creates + saves a colony via
  RimWorld's own worldgen/`Game`/`GameDataSaveLoader` APIs on first run, caching the result under
  `Fixtures/`. Removes the one manual, non-scriptable prerequisite entirely and makes fixtures
  reproducible and diffable. Pairs naturally with the existing `SetTile`/`SetSeason`/`SetTime`
  step vocabulary.
- [ ] **Ship a committed minimal fixture** — as a fallback, a genuinely small vanilla-only colony
  save checked into git (or generated once and committed) so runs don't depend on the user's
  Steam Workshop mod set.
- [ ] **Scene setup: place objects/pawns on load** — a scenario step (or `FixtureSpec` field) to
  spawn specified things at map load: e.g. a row of walls/pillars on flat ground as deliberate
  shadow-casters, or pawns at known positions. The `shadow_lean_equinox` run showed the weakness —
  the fixture's flat sand had almost nothing tall, so the shadow lean was hard to eyeball even
  though the probe passed. Purpose-built casters would make lighting screenshots (shadow
  direction, night darkness, moonlight tint, twilight hue) genuinely reviewable, not just
  numerically gated. Pairs with quickstart mode (generate blank colony → place casters → assert).

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
