# RimWorldTestHarness — design notes

## Problem

Verifying a mod's in-game behavior (does this shadow lean the right way at this latitude on this
day; does this twilight color look right) currently means the developer launches RimWorld, uses
dev-mode tools to set up the scenario by hand, and eyeballs the result — every time, for every
edge case, on every change. That doesn't scale past a couple of manual spot-checks, and it means
regressions can only be caught by a human who remembers to go re-check.

## Approach

Two complementary verification modes, driven by one JSON `ScenarioSpec` format
(`Shared/ScenarioSpec.cs`):

1. **Spec-driven pass/fail.** A scenario lists `Probe` steps: read some named numeric quantity
   (via `Mod/Probes/IProbe`) and compare it against an expected value within a tolerance
   (`Shared/ReportComparer.cs`). A scenario passes only if every probe check passes
   (`ReportComparer.AllPass`). This is the automatable gate — the piece that can run unattended
   in a loop and produce a clean yes/no.

2. **Screenshot visual-confirm.** A scenario can also list `Screenshot` steps, which just capture
   an image to the report folder. These never affect pass/fail — they're for the cases a number
   can't easily capture (does this twilight color actually *look* warm and long, not just measure
   as such) and are meant to be reviewed by a human or by Claude afterward.

3. **Before/after A/B via feature toggles.** A visual effect is hard to judge from a single frame —
   "is this warm?" only means something next to the same scene *without* the effect. A `SetFeature`
   step flips a named runtime flag in the mod under test (through `Mod/Features/FeatureRegistry`,
   the same one-directional bridge as `IProbe`/`ProbeRegistry` — the harness never references the
   mod), so one scenario in a single game boot can screenshot the effect off, flip it on, and
   screenshot again. The two frames come from an identical world/time, so the only difference is the
   effect. The same flags are what the mod's real settings screen drives for users, so the toggle
   seam isn't test-only scaffolding. See `civil_twilight_dusk.json` for the pattern.

All modes share one spec format and one report format (`Shared/ScenarioReport.cs`) so a single
scenario run can produce all of them at once.

## Why a separate project, not bolted onto CelestialLighting

This harness is infrastructure for testing *any* mod in this repo, not specific to one. It follows
the `HarmonyConflictChecker` shape: a dependency-free `Shared` project (pure spec/report/comparer
logic, unit-testable under plain `net8.0`) plus a thin `Mod` adapter (`net481`, the actual
in-game driver) that depends on `Shared` but isn't itself tested directly — the pure logic is
tested, the live-game plumbing is validated by actually running it.

## Architecture

- **`Shared/`** (`netstandard2.0`, no `UnityEngine`/`Verse` dependency) — `ScenarioSpec`/
  `ScenarioStep`/`StepArgs` (the spec format), `ScenarioReport`/`ProbeCheckResult` (the report
  format), `ReportComparer` (pure pass/fail logic), `ScenarioSpecLoader` (JSON parsing). This is
  the part with real edge-case unit tests (`Tests/RimWorldTestHarness.Tests/`), following the same
  pure-core pattern as `CelestialLighting/Source/Formulas.cs`.
- **`Mod/`** (`net481`) — `HarnessMod` (bootstrap, opt-in via `RWTH_SCENARIO` env var so the mod
  is inert in normal play), `ScenarioDriver` (a tick-driven state machine that executes a spec's
  steps against live game state, advanced by `Patch_DriveScenario`'s postfix on
  `Root_Play.Update()` — see "Save loading" below for why it has to be tick-driven rather than
  running synchronously at startup), `Patch_ForceDevMode`/`Patch_ForcedLatitude` (narrow Harmony
  postfixes the driver's `SetTile` step and the vanilla autostart-save check depend on),
  `Probes/IProbe` + `ProbeRegistry` (the extension point other mods use to expose numeric probes),
  `Features/FeatureRegistry` (the parallel extension point for `SetFeature` — mods register named
  `Action<bool>` flag setters so scenarios can toggle effects for before/after screenshots).
- **`Runner/run_test.sh`** — the external driver: launches RimWorld with a scenario, waits for
  completion, gates on the report. Modeled on `MissileGirl/TestMods/run_test.sh`'s proven
  launch/retry/log-wait/cleanup shape, adapted for a lighter single-scenario run and (unlike that
  harness) real GPU rendering, since screenshots need it.
- **`Scenarios/`** — example/reusable `ScenarioSpec` JSON files.
- **`Fixtures/`** — save files scenarios load from. Not auto-creatable (no headless "new colony"
  API — see below); each one is made manually once. Gitignored.

## Live companion channel (interactive mode)

Alongside the batch mode above, the mod has a second, **interactive** mode: a companion channel that
lets an external client (the sibling `RimWorldDevMCP` server, or its `rwdev` CLI) drive dev-actions
against a game that's *already running* — set the time, read a probe, grab a screenshot — one command
at a time, with results returned live. Batch answers "did this scenario pass?"; the companion answers
"let me poke at whatever's on screen right now."

- **Opt-in, not env-var-gated.** Batch keys off `RWTH_SCENARIO`, which a normally-launched game never
  sets. The companion is instead armed by a mod-settings checkbox (`HarnessSettings` /
  `HarnessMain`), so it works against a game started normally through Steam. Off by default; the mod
  stays inert until it's ticked.
- **Same tick pump, mutually exclusive.** `LiveCommandDriver.Tick()` is pumped from the same
  `Patch_DriveScenario` postfix as `ScenarioDriver`. It bails immediately when a batch scenario is
  `Active`, and no-ops (near-zero cost) when the setting is off — the two never run together.
- **Minimally invasive.** Unlike batch, the companion runs against the user's real game, so it never
  forces DevMode (`Patch_ForceDevMode` keys on `HarnessRuntime.ForceDevMode`, which only batch sets)
  and never touches time speed except for an explicit `FastForward` (which restores the prior speed
  when done). Read actions (probe/screenshot/status) are always safe mid-play.
- **Shared executor.** Both drivers run the identical per-action game logic via `StepExecutor` (with
  `HarnessRuntime` holding the flags the patches read), so batch and live can't drift. The batch
  driver folds each `StepOutcome` into a `ScenarioReport`; the live driver folds it into a
  `LiveResponse`.
- **File-queue transport.** Every game-state mutation must happen on the main tick thread, so an
  in-process HTTP listener would have to marshal onto the tick loop anyway. Instead the driver drains
  a request/response file queue under `$XDG_CACHE_HOME/rimworld-dev-mcp/live` each frame, writing
  atomically (tmp + `File.Replace`). The wire contract is `Shared/LiveProtocol.cs` — pure DTOs with
  no transport assumptions, so a later HTTP transport reuses the exact same parsing. See
  `RimWorldDevMCP/DESIGN.md` for the client half.

### Actions live as real dev commands; the catalog is discovered, not hardcoded

Which dev-actions and probes exist depends on the loaded modset, so the companion **discovers** them
rather than shipping a static list. On map load it emits a `catalog.json` built from three sources:
this harness's own verbs, the registered `ProbeRegistry`/`FeatureRegistry` entries, and — crucially —
RimWorld's **own** dev-action registry. `DevActionCatalog` reflects over `Verse.GenTypes.AllTypes` for
`[LudeonTK.DebugAction]`-attributed methods (the same set the game's dev menu shows), so the catalog
mirrors the actual dev menu for whatever mods are loaded.

New harness actions are added as real `[DebugAction]`s too, not bespoke code: `HarnessDebugActions`
registers the screenshot action under a `RimWorldTestHarness` dev-menu category, sharing one capture
core with the live channel. Honest limits: only zero-arg `DebugActionType.Action` entries are
invokable headlessly — tool-type actions (which need a click target) are listed but not yet callable,
and some `Action` entries open a UI dialog rather than doing something headless. The API-compat tests
pin the `LudeonTK` dev-action surface (`DebugActionAttribute`/`DebugActionType`) and the catalog's
data sources, since `LudeonTK` is where RimWorld relocated its debug tooling and is prone to the
silent-breakage pitfall the parent `CLAUDE.md` calls out.

## Where probe tests live

`Mod/Probes/IProbe.cs`/`ProbeRegistry.cs` is the *only* thing this repo owns for probes — the
interface and the registry. A target mod's actual `IProbe` implementation, and any unit tests for
the logic it wraps, belong in **that mod's own repo**, not here. For the reference case,
CelestialLighting's shadow-lean probe lives at
`CelestialLighting/Source/Probes/ShadowLeanProbe.cs` — a one-line delegation to the already-pure,
already-tested `LatitudeEffect.ForMap(map).Lean` (`CelestialLighting/Source/Formulas.cs` +
`CelestialLighting/Tests/CelestialLighting.Tests/FormulasTests.cs` already cover the actual math,
so the probe itself gets no separate unit test — same pure-core-plus-thin-adapter split
`Formulas.cs`/`FormulasTests.cs` already uses). This repo's own `Tests/` project stays scoped to
what it actually owns (the spec/report/comparer format) and should never accumulate another mod's
domain-logic tests.

Corollary: a target mod's **shipped** DLL should not take a hard reference to
`RimWorldTestHarness.Mod` just to implement `IProbe` — that would make a dev-only test tool a
runtime dependency of a real mod players install. `CelestialLighting/Source/CelestialLighting.csproj`
excludes `Probes/**/*.cs` from its own compile glob (`<Compile Remove>`); a separate
`CelestialLighting/TestMod/CelestialLighting.Probes.csproj` (packageId
`joof.celestiallighting.probes`, hard `modDependencies` on both CelestialLighting and this
harness) links that same file back in and registers it via `ProbeRegistry.Register` in a
`[StaticConstructorOnStartup]` — mirroring `MissileGirl/TestMods/`'s purpose-built-test-mod
pattern rather than adding the reference to the main mod project.

## Save loading: the vanilla autostart mechanism

`Verse.QuickStarter` (`-quicktest`) only calls `SceneManager.LoadScene("Play")` — confirmed by
decompiling `Assembly-CSharp.dll`. It does **not** set up a game or load a save, so it's a dead
end for this harness's purposes.

What actually works is RimWorld's own **autostart save mechanism**, already built into
`Verse.Root_Entry.Start()`: if `Prefs.DevMode` is true and a save file named exactly `autostart`
(case-insensitive) exists under `GenFilePaths.SavedGamesFolderPath`
(`SaveGameFilesUtility.GetAutostartSaveFile()`), vanilla code loads it itself
(`GameDataSaveLoader.LoadGame(FileInfo)`) before any of our code runs. No custom loader is needed
— `Patch_ForceDevMode.cs` (a Harmony postfix scoped to `ScenarioDriver.Active`, so it never
touches the user's real `Prefs.xml`) makes `Prefs.DevMode` read `true` while a scenario is active,
and `Runner/run_test.sh` copies the fixture save to `Saves/autostart.rws` before launch.

This is also *why* `ScenarioDriver` has to be a tick-driven state machine rather than running
synchronously: `HarnessMod`'s `[StaticConstructorOnStartup]` fires before any scene exists, well
before `Root_Entry.Start()` even runs its autostart check, let alone before a map is loaded. So
`ScenarioDriver.Begin()` only sets `Active = true` and stashes the spec; actual step execution
waits for `Patch_DriveScenario`'s postfix on `Root_Play.Update()` to observe `Find.CurrentMap !=
null` before advancing past `State.WaitingForMap`.

## Screenshot capture requires GPU rendering

`ScreenCapture.CaptureScreenshot` needs the game actually rendering a frame, which rules out a
pure headless/`-batchmode -nographics` launch (the mode `MissileGirl/TestMods/run_test.sh` uses,
since it only needs Player.log output). `Runner/run_test.sh` launches normally instead.

## API compatibility tests

`Tests/RimWorldTestHarness.ApiTests/` (Mono.Cecil, pattern:
`PerformanceSearch/Tests/SearchFix.Tests/`) pins every vanilla member `Mod/` depends on —
`Prefs.DevMode`, `WorldGrid.LongLatOf(PlanetTile)`, `Root_Play.Update`, `GenDate`'s tick constants
and `DayOfYear`/`HourFloat`/`LocalTicksOffsetFromLongitude`, `TickManager`'s tick/speed members,
`LongEventHandler.ShouldWaitForEvent`, `ScreenCapture.CaptureScreenshot` — plus the vanilla
autostart-save chain (`Root_Entry.Start`, `SaveGameFilesUtility.GetAutostartSaveFile`,
`GameDataSaveLoader.LoadGame(FileInfo)`) that "Save loading" above depends on even though nothing
in `Mod/` calls it directly. `ScreenCapture` lives in a separate `UnityEngine.ScreenCaptureModule.dll`
alongside `Assembly-CSharp.dll`, not inside it, so this project loads two Mono.Cecil
`ModuleDefinition`s. A separate project from `Tests/RimWorldTestHarness.Tests/` (rather than one
project like `PerformanceSearch` uses) because these tests are `[Category("RequiresGameDll")]` and
`Assert.Ignore` themselves out when the real DLLs aren't present; `test.sh` runs both projects.
