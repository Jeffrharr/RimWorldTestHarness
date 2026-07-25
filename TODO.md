# TODO

Status: all pieces are implemented and offline-tested (`Shared/`, `Mod/`, `Runner/run_test.sh`,
`Tests/RimWorldTestHarness.Tests`, `Tests/RimWorldTestHarness.ApiTests`). Remaining work:

- [x] **First fixture save** — `Fixtures/minimal_colony.rws` is now in place, copied from the
  user's newest live colony save ("Tehenussia Unification"). Note it's a real, mod-heavy
  permadeath colony, *not* a minimal one — good enough to unblock live runs, but see the fixture
  ideas below for why we'll want something leaner and reproducible.
- [x] **Live end-to-end run** — `Runner/run_test.sh ../CelestialLighting/Tests/Scenarios/shadow_lean_equinox.json` passes
  against the fixture: exits 0, `shadow_lean: PASS` (actual `1.02e-06`, tol `0.02`), screenshot in
  `Runner/reports/`. Confirmed the mod-heavy fixture loads cleanly under the minimal ModsConfig.
- [x] **Verify timelapse settle frames against a real run** — confirmed live via CelestialLighting's
  `Tests/Scenarios/civil_twilight_dusk_timelapse.json` (two 24-frame dusk sweeps, lat 75 day 15, feature
  off then on). Across all 48 frames `settleFrames: 2` showed no smearing or half-updated lighting
  after each clock jump, and `screenshotMode` cleanly blanked the HUD (no toolbar/alerts/date
  readout/colonist bar). Both mp4s stitched fine at 8fps. Raise `settleFrames` only if a future
  denser/faster sweep smears.
- [x] **Give the timelapse something to look at** — done and confirmed live.
  `Scenarios/shadow_casters_daycycle.json` paints a 40×40 concrete pad, drops a 4×4 granite pillar
  grid on it, aims the camera at it and sweeps a full day at half-hour steps.
  `Runner/run_test.sh Scenarios/shadow_casters_daycycle.json` exits 0, writes 48 frames and stitches
  `casters.mp4`. Checked by eye: all 16 pillars present on the pad, and the shadows genuinely read —
  at 09:00 (frame 0018) they're short and lean north-east, by 17:00 (frame 0034) they've swung west,
  lengthened several-fold and the light has gone warm. No smearing at `settleFrames: 2`, and
  `screenshotMode` blanked the HUD. This is the thing `shadow_lean_equinox`'s screenshot couldn't
  show: the lean is now reviewable, not just numerically gated.

- [ ] **Gate on RimWorld's own logged errors** — surfaced while debugging the quickstart blockers:
  `ReportComparer.AllPass(checks, errors)` catches failing steps, but nothing catches the *game*
  logging 12 root-level exceptions and rendering no map — that run still exited 0. Worth having
  `ScenarioDriver` (or `run_test.sh`, which already has Player.log in hand) treat
  `"Root level exception"` / `Log.Error` output during a scenario as a run failure, since that is
  precisely the plausible-looking-but-wrong outcome the harness exists to catch.

## Fixture ideas (future)

The current fixture is whatever save happened to be newest — heavy, tied to the user's exact
active mod list, and not something a fresh checkout can regenerate. Directions to make the
fixture story self-serve instead of manual:

- [ ] **Quickstart / no-fixture mode** — the three blockers that made this unusable are all
  implemented. The first two are **confirmed live** (2026-07-24, four runs); the third
  ([#4](https://github.com/Jeffrharr/RimWorldTestHarness/issues/4), the `clear` arg) is **offline-tested
  only and still needs a live run**. What to check when it gets one: a `-quicktest` scenario with
  `"clear": "true"` on both `SetTerrain` and `PlaceThings` should place every pillar, and the pad should
  read as open sky rather than carrying the darker patch of a surviving overhead-mountain region. Worth
  confirming from Player.log at the same time that a run *without* `clear` emits the roofed-footprint
  `Log.Warning`, because that warning is the only thing standing between a forgotten `clear` and a green
  run over a wrongly-lit screenshot. Note there is still no committed quickstart scenario — the run in
  \#4 used an ad-hoc one — which is the remaining piece of this item.

  What was fixed:
  - `ScenarioDriver` waited only for `Find.CurrentMap != null`, which goes true partway through
    `Game.InitNewGame` — so steps ran during `MapInitializing`, producing 12 ×
    `ArgumentOutOfRangeException` from `Verse.TickList.Tick` and no rendered map, while the run still
    reported `Pass: true`. Now gated on `ProgramState.Playing` (set by `Game.FinalizeInit`, which both
    `InitNewGame` and `LoadGame` reach) plus settle frames. Verified: 0 exceptions, and no magic `Wait`
    needed in the scenario. See `DESIGN.md`, "Readiness is `ProgramState.Playing`".
  - Map centre on a fresh colony is fogged, and RimWorld draws neither terrain nor things in fogged
    cells — so the scene built correctly and was invisible, with every step reporting success. Scene
    steps now lift fog by default (`unfog`).
  - A generated colony often has rock where the scenario wants its pad (`placed 12 of 16 Wall —
    4 refused`), and mountain cells carry overhead roof that darkens exactly the ground a lighting
    scenario cares about — silently, since terrain and pillars go in perfectly well underneath it.
    `PlaceThings`/`SetTerrain` now take `clear`, which destroys the destroyable things in the footprint
    and strips its roof. Opt-in (not opt-out like `unfog`) because clearing deletes map content and the
    same cores are reachable from a dev action pointed at a real colony; a roofed footprint still
    `Log.Warning`s when `clear` was omitted, so the safe default buys no silence. See `DESIGN.md`,
    "Clearing the footprint is opt-in".

  Original rationale, still valid (lightest — for when the colony doesn't matter): for
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
- [ ] **Scene setup: place pawns at known positions** — the things/terrain/camera half of scene setup
  is done (see Done below), but pawns were deliberately left out: `PawnGenerator.GeneratePawn` drags in
  faction resolution and Biotech xenotype args, a meaningfully larger surface than thing-spawning and
  not needed for the shadow-caster case that motivated the work. A `PlacePawn` step would reuse
  `SceneLayout`'s layout vocabulary (`grid`/`row`/`cells`, anchor, offset) completely unchanged — only
  a new adapter path in `Mod/SceneBuilder.cs` and a new `Dispatch` case are actually missing. Worth
  doing when a scenario needs pawns in frame (checking night-time pawn visibility, or that a lighting
  change doesn't wash out skin tones).

## Done

- [x] **Scene setup: things, terrain and camera** — three new steps, `PlaceThings` / `SetTerrain` /
  `LookAt`. Layout arithmetic and arg validation are pure (`Shared/SceneLayout.cs`, offline-tested);
  `Mod/SceneBuilder.cs` is the Verse-touching adapter and also exposes a "Place shadow-caster grid"
  `[DebugAction]` over the same core. Layouts are `grid` (default, separate pillars so each throws its
  own readable shadow), `row` and an explicit `cells` escape hatch; coordinates are anchor-relative
  offsets defaulting to map centre, so a scenario is fixture-independent. Spawned at runtime rather
  than authored into the save XML — see `DESIGN.md`'s "Scene setup" section for why, and note the XML
  route is still the right tool for the fixture-generation ideas above.
- [x] **Step errors count toward Pass** — `ReportComparer.AllPass(checks, errors)`, used by
  `ScenarioDriver.Finish()`. Previously `Pass` looked only at probe checks, and `AllPass` over an empty
  list is `true`, so a scenario with no `Probe` step reported `Pass: true` and the runner exited 0 even
  if every step had errored. That mattered most for image-only runs: a failed `PlaceThings` would have
  left a plausible-looking empty screenshot on a green run.
- [x] **Load-time step validation** — `Shared/StepValidator.cs`, run at `ScenarioSpecLoader`'s existing
  choke point. Dry-runs the (pure) scene planners and rejects unknown step types, which finally makes
  true the claim `ScenarioSpec.cs` has always carried that the loader validates step types.

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
